package com.scrcpybar.companion.net

import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.BufferedInputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.Socket
import java.net.URL

/**
 * Protocol level tests for [MiniHttpServer]. Runs on the JVM, no device required.
 */
class MiniHttpServerTest {

    private lateinit var server: MiniHttpServer
    private var port: Int = 0

    @Before
    fun setUp() {
        server = MiniHttpServer(0) { request ->
            when (request.path) {
                "/echo" -> HttpResponse.json(
                    200,
                    JSONObject().apply {
                        put("method", request.method)
                        put("path", request.path)
                        put("query", request.queryParam("q") ?: JSONObject.NULL)
                        put("body", request.bodyText())
                        put("auth", request.accessToken() ?: JSONObject.NULL)
                    }
                )

                "/bytes" -> HttpResponse.bytes(200, "image/jpeg", byteArrayOf(1, 2, 3, 4))
                "/teapot" -> HttpResponse.text(418, "short and stout")
                else -> HttpResponse.json(
                    404,
                    JSONObject().apply { put("ok", false); put("error", "not found") }
                )
            }
        }
        assertTrue(server.start())
        port = server.boundPort
    }

    @After
    fun tearDown() {
        server.stop()
    }

    @Test
    fun `bound port is ephemeral and non zero`() {
        assertTrue("expected an ephemeral port", port > 0)
        assertTrue(server.isRunning)
    }

    @Test
    fun `GET returns json body and content length`() {
        val response = rawRequest("GET /echo?q=hello HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertTrue(response.statusLine, response.statusLine.startsWith("HTTP/1.1 200"))
        val json = response.json()
        assertEquals("GET", json.getString("method"))
        assertEquals("/echo", json.getString("path"))
        assertEquals("hello", json.getString("query"))
        assertEquals(response.body.size.toString(), response.headers["content-length"])
        assertTrue(response.headers["content-type"]!!.startsWith("application/json"))
    }

    @Test
    fun `POST with content length is parsed`() {
        val body = """{"action":"seek","positionMs":60000}"""
        val request = buildString {
            append("POST /echo HTTP/1.1\r\nHost: localhost\r\n")
            append("Content-Type: application/json\r\n")
            append("Content-Length: ${body.toByteArray().size}\r\n")
            append("Connection: close\r\n\r\n")
            append(body)
        }
        val response = rawRequest(request)
        val json = response.json()
        assertEquals("POST", json.getString("method"))
        assertEquals(body, json.getString("body"))
    }

    @Test
    fun `POST with chunked body is parsed`() {
        val request = buildString {
            append("POST /echo HTTP/1.1\r\nHost: localhost\r\n")
            append("Transfer-Encoding: chunked\r\n")
            append("Connection: close\r\n\r\n")
            append("5\r\n").append("hello").append("\r\n")
            append("1\r\n").append("!").append("\r\n")
            append("0\r\n\r\n")
        }
        val response = rawRequest(request)
        assertEquals("hello!", response.json().getString("body"))
    }

    @Test
    fun `expect 100-continue handshake`() {
        val body = """{"action":"play"}"""
        val socket = Socket("127.0.0.1", port)
        val output = socket.getOutputStream()
        output.write(
            ("POST /echo HTTP/1.1\r\nHost: localhost\r\nExpect: 100-continue\r\n" +
                "Content-Length: ${body.toByteArray().size}\r\nConnection: close\r\n\r\n").toByteArray()
        )
        output.flush()

        val first = readLine(socket.getInputStream())
        assertEquals("HTTP/1.1 100 Continue", first)

        output.write(body.toByteArray())
        output.flush()

        val response = readAll(socket.getInputStream())
        socket.close()
        assertTrue(response.contains("200 OK"))
        assertTrue(response.contains("play"))
    }

    @Test
    fun `bearer token and query token are both accepted`() {
        val withHeader = rawRequest(
            "GET /echo HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer abc123\r\n" +
                "Connection: close\r\n\r\n"
        )
        assertEquals("abc123", withHeader.json().getString("auth"))

        val withQuery = rawRequest("GET /echo?token=xyz HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals("xyz", withQuery.json().getString("auth"))
    }

    @Test
    fun `keep-alive serves two requests on one connection`() {
        val socket = Socket("127.0.0.1", port)
        val output = socket.getOutputStream()
        val input = BufferedInputStream(socket.getInputStream())

        repeat(2) { index ->
            output.write("GET /echo?q=$index HTTP/1.1\r\nHost: localhost\r\n\r\n".toByteArray())
            output.flush()
            val response = readResponse(input)
            assertEquals(200, response.status)
            assertEquals("$index", response.json().getString("query"))
        }
        socket.close()
    }

    @Test
    fun `HEAD returns headers without body`() {
        val response = rawRequest("HEAD /bytes HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals(200, response.status)
        assertEquals("4", response.headers["content-length"])
        assertEquals(0, response.body.size)
    }

    @Test
    fun `binary body is delivered verbatim`() {
        val response = rawRequest("GET /bytes HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals("image/jpeg", response.headers["content-type"])
        assertArrayEqualsBytes(byteArrayOf(1, 2, 3, 4), response.body)
    }

    @Test
    fun `404 json error is returned`() {
        val response = rawRequest("GET /nope HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals(404, response.status)
        assertFalse(response.json().getBoolean("ok"))
    }

    @Test
    fun `unsupported method is rejected without killing the server`() {
        val bad = rawRequest("DELETE /echo HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals(501, bad.status)

        val good = rawRequest("GET /echo HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        assertEquals(200, good.status)
    }

    @Test
    fun `HttpURLConnection can post json and read the reply`() {
        val connection = URL("http://127.0.0.1:$port/echo").openConnection() as HttpURLConnection
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Authorization", "Bearer token-42")
        connection.setRequestProperty("Content-Type", "application/json")
        val payload = """{"action":"seek","positionMs":60000}"""
        connection.outputStream.use { it.write(payload.toByteArray()) }

        assertEquals(200, connection.responseCode)
        val json = JSONObject(connection.inputStream.bufferedReader().readText())
        assertEquals(payload, json.getString("body"))
        assertEquals("token-42", json.getString("auth"))
        connection.disconnect()
    }

    @Test
    fun `stop releases the port`() {
        server.stop()
        assertFalse(server.isRunning)
        var failed = false
        try {
            Socket("127.0.0.1", port).close()
        } catch (e: Exception) {
            failed = true
        }
        assertTrue("port should be closed after stop()", failed)
    }

    // ---------------------------------------------------------------- helpers

    private class Parsed(
        val statusLine: String,
        val status: Int,
        val headers: Map<String, String>,
        val body: ByteArray
    ) {
        fun json(): JSONObject = JSONObject(String(body))
    }

    private fun rawRequest(raw: String): Parsed {
        val socket = Socket("127.0.0.1", port)
        socket.soTimeout = 10_000
        val output = socket.getOutputStream()
        output.write(raw.toByteArray())
        output.flush()
        val head = raw.substringBefore("\r\n")
        val method = head.substringBefore(' ')
        val parsed = readResponse(BufferedInputStream(socket.getInputStream()), expectBody = method != "HEAD")
        socket.close()
        return parsed
    }

    private fun readResponse(input: InputStream, expectBody: Boolean = true): Parsed {
        val statusLine = readLine(input) ?: error("no status line")
        val status = statusLine.split(' ').getOrNull(1)?.toIntOrNull() ?: -1

        val headers = LinkedHashMap<String, String>()
        while (true) {
            val line = readLine(input) ?: break
            if (line.isEmpty()) break
            val separator = line.indexOf(':')
            headers[line.substring(0, separator).trim().lowercase()] = line.substring(separator + 1).trim()
        }

        // HEAD carries the entity Content-Length of the body that *would* have been sent.
        val length = if (expectBody) headers["content-length"]?.toIntOrNull() ?: 0 else 0
        val body = ByteArray(length)
        var read = 0
        while (read < length) {
            val chunk = input.read(body, read, length - read)
            if (chunk < 0) break
            read += chunk
        }
        return Parsed(statusLine, status, headers, body)
    }

    private fun readLine(input: InputStream): String? {
        val builder = StringBuilder()
        while (true) {
            val char = input.read()
            if (char < 0) return if (builder.isEmpty()) null else builder.toString()
            if (char == '\n'.code) return builder.toString().removeSuffix("\r")
            builder.append(char.toChar())
        }
    }

    private fun readAll(input: InputStream): String =
        input.bufferedReader().readText()

    private fun assertArrayEqualsBytes(expected: ByteArray, actual: ByteArray) {
        assertEquals(expected.size, actual.size)
        for (i in expected.indices) assertEquals("byte $i", expected[i], actual[i])
    }
}