package com.scrcpybar.companion.net

import android.util.Log
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.BindException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.nio.charset.StandardCharsets
import java.util.Locale
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * Small blocking HTTP/1.1 server.
 *
 * Implemented directly on [ServerSocket] so the companion app stays dependency free: the desktop
 * bridge only needs to be able to talk plain HTTP to the phone.
 *
 * Supports keep-alive, `Content-Length` and `Transfer-Encoding: chunked` request bodies and the
 * `Expect: 100-continue` handshake, which is what `HttpURLConnection`, `HttpClient` and `curl`
 * send in practice.
 */
class MiniHttpServer(
    private val port: Int,
    private val dispatcher: (HttpRequest) -> HttpResponse
) {

    @Volatile
    private var running = false

    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null
    private val startedAtNanos = System.nanoTime()

    @Volatile
    var lastError: String? = null
        private set

    val isRunning: Boolean
        get() = running

    val boundPort: Int
        get() = serverSocket?.localPort ?: port

    val uptimeMs: Long
        get() = if (running) (System.nanoTime() - startedAtNanos) / 1_000_000L else 0L

    private var pool: ThreadPoolExecutor? = null

    fun start(): Boolean {
        if (running) return true
        pool = ThreadPoolExecutor(
            2, 16, 30L, TimeUnit.SECONDS, ArrayBlockingQueue<Runnable>(64),
            { runnable -> Thread(runnable, "scrcpybar-http-${counter.incrementAndGet()}").apply { isDaemon = true } },
            ThreadPoolExecutor.AbortPolicy()
        )
        val socket = try {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(InetAddress.getByName(BIND_ADDRESS), port), BACKLOG)
            }
        } catch (e: BindException) {
            lastError = "port $port is already in use"
            Log.w(TAG, "cannot bind port $port", e)
            return false
        } catch (e: IOException) {
            lastError = e.message ?: e.javaClass.simpleName
            Log.w(TAG, "cannot start server", e)
            return false
        }

        serverSocket = socket
        running = true
        lastError = null
        acceptThread = Thread({ acceptLoop(socket) }, "scrcpybar-http-accept").apply {
            isDaemon = true
            start()
        }
        Log.i(TAG, "listening on ${BIND_ADDRESS}:${socket.localPort}")
        return true
    }

    fun stop() {
        running = false
        runCatching { serverSocket?.close() }
        serverSocket = null
        acceptThread = null
        pool?.shutdownNow()
        pool = null
        Log.i(TAG, "server stopped")
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (running && !socket.isClosed) {
            val client = try {
                socket.accept()
            } catch (e: SocketException) {
                break
            } catch (e: IOException) {
                if (running) Log.w(TAG, "accept failed", e)
                continue
            }
            try {
                pool?.execute { serve(client) }
            } catch (e: RejectedExecutionException) {
                runCatching { writeAndClose(client, HttpResponse.text(503, "server busy")) }
            }
        }
    }

    private fun serve(socket: Socket) {
        try {
            socket.tcpNoDelay = true
            socket.soTimeout = SOCKET_TIMEOUT_MS
            val input = BufferedInputStream(socket.getInputStream(), BUFFER_SIZE)
            val output = BufferedOutputStream(socket.getOutputStream(), BUFFER_SIZE)
            val remote = socket.inetAddress?.hostAddress ?: "?"

            while (running && !socket.isClosed) {
                val request = readRequest(input, output, remote)
                val keepAlive = shouldKeepAlive(request) && running

                val response = try {
                    dispatcher(request)
                } catch (t: Throwable) {
                    Log.e(TAG, "dispatcher failed for ${request.method} ${request.path}", t)
                    errorBody(500, "internal error")
                }

                writeResponse(output, response, keepAlive, headOnly = request.method == "HEAD")
                output.flush()
                if (!keepAlive) return
            }
        } catch (_: SocketTimeoutException) {
            // idle keep-alive connection, nothing to report
        } catch (_: EOFException) {
            // client disconnected
        } catch (_: SocketException) {
            // connection reset
        } catch (e: HttpProtocolException) {
            runCatching { clientFallback(socket, e) }
        } catch (e: IOException) {
            Log.d(TAG, "connection error: ${e.message}")
        } finally {
            runCatching { socket.close() }
        }
    }

    private fun clientFallback(socket: Socket, e: HttpProtocolException) {
        writeAndClose(socket, errorBody(e.status, e.message))
    }

    private fun writeAndClose(socket: Socket, response: HttpResponse) {
        runCatching {
            socket.soTimeout = WRITE_TIMEOUT_MS
            val output = BufferedOutputStream(socket.getOutputStream(), BUFFER_SIZE)
            writeResponse(output, response, keepAlive = false, headOnly = false)
            output.flush()
        }
        runCatching { socket.close() }
    }

    // ---------------------------------------------------------------- request parsing

    private fun readRequest(input: InputStream, output: OutputStream, remote: String): HttpRequest {
        val requestLine = readAsciiLine(input) ?: throw EOFException("connection closed")
        if (requestLine.isEmpty()) throw EOFException("connection closed")

        val parts = requestLine.split(' ')
        if (parts.size < 2) throw HttpProtocolException(400, "malformed request line")
        val method = parts[0].uppercase(Locale.US)
        val target = parts[1]
        val version = parts.getOrNull(2) ?: "HTTP/1.0"

        if (method !in SUPPORTED_METHODS) throw HttpProtocolException(501, "method $method is not supported")

        val headers = LinkedHashMap<String, String>()
        var headerBytes = requestLine.length
        while (true) {
            val line = readAsciiLine(input) ?: throw EOFException("connection closed in headers")
            if (line.isEmpty()) break
            headerBytes += line.length
            if (headerBytes > MAX_HEADER_BYTES) throw HttpProtocolException(431, "headers too large")
            val separator = line.indexOf(':')
            if (separator <= 0) throw HttpProtocolException(400, "malformed header")
            val name = line.substring(0, separator).trim().lowercase(Locale.US)
            val value = line.substring(separator + 1).trim()
            headers[name] = if (headers.containsKey(name)) headers[name] + ", " + value else value
        }

        if (headers["expect"]?.equals("100-continue", ignoreCase = true) == true) {
            output.write("HTTP/1.1 100 Continue\r\n\r\n".toByteArray(StandardCharsets.US_ASCII))
            output.flush()
        }

        val transferEncoding = headers["transfer-encoding"]
        val contentEncoding = headers["content-encoding"]
        if (contentEncoding != null && !contentEncoding.equals("identity", ignoreCase = true)) {
            throw HttpProtocolException(415, "unsupported content-encoding: $contentEncoding")
        }

        val body = when {
            transferEncoding != null && transferEncoding.contains("chunked", ignoreCase = true) ->
                readChunkedBody(input)
            headers.containsKey("content-length") -> readFixedBody(input, headers["content-length"])
            else -> ByteArray(0)
        }

        val questionMark = target.indexOf('?')
        val path = if (questionMark >= 0) target.substring(0, questionMark) else target
        val query = if (questionMark >= 0) parseQuery(target.substring(questionMark + 1)) else emptyMap()

        return HttpRequest(
            method = method,
            target = target,
            version = version,
            path = normalizePath(path),
            query = query,
            headers = headers,
            body = body,
            remoteAddress = remote
        )
    }

    private fun parseQuery(raw: String): Map<String, String> {
        if (raw.isEmpty()) return emptyMap()
        val result = LinkedHashMap<String, String>()
        for (pair in raw.split('&')) {
            if (pair.isEmpty()) continue
            val equals = pair.indexOf('=')
            val key = if (equals >= 0) pair.substring(0, equals) else pair
            val value = if (equals >= 0) pair.substring(equals + 1) else ""
            result[urlDecode(key)] = urlDecode(value)
        }
        return result
    }

    private fun urlDecode(value: String): String {
        if (value.indexOf('%') < 0 && value.indexOf('+') < 0) return value
        val bytes = ByteArrayOutputStream(value.length)
        var i = 0
        while (i < value.length) {
            val char = value[i]
            when {
                char == '+' -> bytes.write(' '.code)
                char == '%' && i + 2 < value.length -> {
                    val decoded = value.substring(i + 1, i + 3).toIntOrNull(16)
                    if (decoded != null) {
                        bytes.write(decoded)
                        i += 2
                    } else {
                        bytes.write(char.code)
                    }
                }
                else -> bytes.write(char.toString().toByteArray(StandardCharsets.UTF_8))
            }
            i++
        }
        return String(bytes.toByteArray(), StandardCharsets.UTF_8)
    }

    private fun normalizePath(path: String): String =
        if (path.length > 1 && path.endsWith('/')) path.dropLast(1) else path

    private fun readFixedBody(input: InputStream, rawLength: String?): ByteArray {
        val length = rawLength?.trim()?.toIntOrNull()
            ?: throw HttpProtocolException(400, "invalid Content-Length")
        if (length < 0) throw HttpProtocolException(400, "invalid Content-Length")
        if (length == 0) return ByteArray(0)
        if (length > MAX_BODY_BYTES) throw HttpProtocolException(413, "request body too large")
        return readFully(input, length)
    }

    private fun readChunkedBody(input: InputStream): ByteArray {
        val body = ByteArrayOutputStream()
        while (true) {
            val sizeLine = readAsciiLine(input) ?: throw EOFException("connection closed in chunk")
            val sizeText = sizeLine.substringBefore(';').trim()
            val size = sizeText.toIntOrNull(16) ?: throw HttpProtocolException(400, "invalid chunk size")
            if (size == 0) {
                // consume trailers up to the terminating empty line
                while (true) {
                    val trailer = readAsciiLine(input) ?: break
                    if (trailer.isEmpty()) break
                }
                return body.toByteArray()
            }
            if (body.size() + size > MAX_BODY_BYTES) throw HttpProtocolException(413, "request body too large")
            body.write(readFully(input, size))
            val crlf = readAsciiLine(input) ?: throw EOFException("connection closed after chunk")
            if (crlf.isNotEmpty()) throw HttpProtocolException(400, "malformed chunk terminator")
        }
    }

    private fun readAsciiLine(input: InputStream): String? {
        val buffer = ByteArrayOutputStream(128)
        while (true) {
            val read = input.read()
            if (read < 0) {
                return if (buffer.size() == 0) null else buffer.toString(StandardCharsets.US_ASCII.name())
            }
            if (read == '\n'.code) {
                val text = buffer.toString(StandardCharsets.US_ASCII.name())
                return if (text.endsWith("\r")) text.dropLast(1) else text
            }
            buffer.write(read)
            if (buffer.size() > MAX_LINE_BYTES) throw HttpProtocolException(431, "header line too long")
        }
    }

    private fun readFully(input: InputStream, length: Int): ByteArray {
        val data = ByteArray(length)
        var offset = 0
        while (offset < length) {
            val read = input.read(data, offset, length - offset)
            if (read < 0) throw EOFException("unexpected end of body")
            offset += read
        }
        return data
    }

    // ---------------------------------------------------------------- response writing

    private fun shouldKeepAlive(request: HttpRequest): Boolean {
        val connection = request.header("connection") ?: return request.version == "HTTP/1.1"
        if (connection.equals("close", ignoreCase = true)) return false
        return connection.equals("keep-alive", ignoreCase = true) || request.version == "HTTP/1.1"
    }

    private fun writeResponse(output: OutputStream, response: HttpResponse, keepAlive: Boolean, headOnly: Boolean) {
        val head = StringBuilder(256)
        head.append("HTTP/1.1 ").append(response.status).append(' ')
            .append(HttpResponse.reason(response.status)).append("\r\n")
        head.append("Content-Type: ").append(response.contentType).append("\r\n")
        head.append("Content-Length: ").append(response.body.size).append("\r\n")
        head.append("Connection: ").append(if (keepAlive) "keep-alive" else "close").append("\r\n")
        head.append("Access-Control-Allow-Origin: *\r\n")
        head.append("Access-Control-Allow-Headers: Authorization, Content-Type\r\n")
        head.append("Access-Control-Allow-Methods: GET, POST, HEAD, OPTIONS\r\n")
        for ((name, value) in response.headers) {
            head.append(name).append(": ").append(value).append("\r\n")
        }
        head.append("\r\n")

        output.write(head.toString().toByteArray(StandardCharsets.US_ASCII))
        if (!headOnly) output.write(response.body)
    }

    private fun errorBody(status: Int, message: String): HttpResponse =
        HttpResponse.json(status, org.json.JSONObject().apply {
            put("ok", false)
            put("error", message)
            put("status", status)
        })

    private class HttpProtocolException(val status: Int, override val message: String) : IOException(message)

    companion object {
        private const val TAG = "ScrcpyBarHttp"
        private const val BIND_ADDRESS = "127.0.0.1"
        private const val BACKLOG = 64
        private const val BUFFER_SIZE = 16 * 1024
        private const val MAX_HEADER_BYTES = 16 * 1024
        private const val MAX_LINE_BYTES = 8 * 1024
        private const val MAX_BODY_BYTES = 1024 * 1024
        private const val SOCKET_TIMEOUT_MS = 30_000
        private const val WRITE_TIMEOUT_MS = 5_000

        private val SUPPORTED_METHODS = setOf("GET", "POST", "HEAD", "OPTIONS")
        private val counter = AtomicInteger(0)
    }
}