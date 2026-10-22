# ScrcpyBar companion has no reflection-based serialization, no JNI and no
# dynamically loaded classes, so the default Android rules are sufficient.
# Keep the notification listener entry point name stable.
-keep class com.scrcpybar.companion.media.ScrcpyMediaListenerService { *; }