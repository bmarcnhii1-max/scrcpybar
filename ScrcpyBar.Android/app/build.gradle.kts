import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.scrcpybar.companion"
    compileSdk = 35
    buildToolsVersion = "34.0.0"

    defaultConfig {
        applicationId = "com.scrcpybar.companion"
        minSdk = 24
        targetSdk = 35
        versionCode = 3
        versionName = "1.0.3"

        buildConfigField("int", "DEFAULT_PORT", "38080")
        buildConfigField("String", "API_VERSION", "\"1\"")
    }

    // Shared debug key lives in the repo so local and CI builds carry the same
    // signature and install as updates over each other. Debug-only, never trusted.
    signingConfigs {
        create("sharedDebug") {
            storeFile = rootProject.file("debug.keystore")
            storePassword = "scrcpybar"
            keyAlias = "scrcpybar-debug"
            keyPassword = "scrcpybar"
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("sharedDebug")
        }
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        viewBinding = true
        buildConfig = true
    }

    testOptions {
        unitTests {
            isReturnDefaultValues = true
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")

    testImplementation("junit:junit:4.13.2")
    // Real org.json shadows the android.jar stub so the router can be exercised off-device.
    testImplementation("org.json:json:20231013")
}