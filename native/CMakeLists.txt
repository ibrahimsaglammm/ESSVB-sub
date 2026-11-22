# Copyright (c) 2026 ESSVB. All Rights Reserved.
# Build command (Windows MSYS2 mingw64 or CachyOS Arch Linux):
#   cmake -S native -B build/native -G Ninja -DCHIAKI_SOURCE_DIR=/path/to/chiaki-ng
#   cmake --build build/native

cmake_minimum_required(VERSION 3.20)
project(essvb_rp C)

set(CHIAKI_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../couchsubs-main/third-party/chiaki-ng" CACHE PATH "Path to chiaki-ng source tree")
set(FFMPEG_PREFIX "" CACHE PATH "Optional FFmpeg prefix (include/, lib/)")

if(NOT WIN32)
    set(CMAKE_POSITION_INDEPENDENT_CODE ON)
else()
    set(CMAKE_FIND_LIBRARY_SUFFIXES .a)
    set(OPENSSL_USE_STATIC_LIBS ON CACHE BOOL "" FORCE)
    add_compile_definitions(MINIUPNP_STATICLIB)
endif()

foreach(opt CHIAKI_ENABLE_GUI CHIAKI_ENABLE_CLI CHIAKI_ENABLE_TESTS CHIAKI_ENABLE_FFMPEG_DECODER
        CHIAKI_ENABLE_PI_DECODER CHIAKI_LIB_ENABLE_OPUS CHIAKI_ENABLE_SPEEX CHIAKI_ENABLE_SETSU
        CHIAKI_ENABLE_STEAMDECK_NATIVE CHIAKI_ENABLE_STEAM_SHORTCUT CHIAKI_ENABLE_BOREALIS
        BUILD_SHARED_LIBS USE_NGHTTP2 USE_LIBIDN2 CURL_ZSTD CURL_BROTLI CURL_USE_LIBPSL CURL_USE_LIBSSH2
        USE_LIBRTMP CURL_ZLIB)
    set(${opt} OFF CACHE STRING "" FORCE)
endforeach()
set(CURL_DISABLE_LDAP ON CACHE BOOL "" FORCE)

add_subdirectory("${CHIAKI_SOURCE_DIR}" chiaki EXCLUDE_FROM_ALL)

foreach(lib avcodec swscale avutil)
    find_library(FFMPEG_${lib} NAMES ${lib} HINTS "${FFMPEG_PREFIX}/lib" NO_DEFAULT_PATH)
    if(NOT FFMPEG_${lib})
        find_library(FFMPEG_${lib} NAMES ${lib} REQUIRED)
    endif()
endforeach()
find_path(FFMPEG_INCLUDE_DIR libavcodec/avcodec.h HINTS "${FFMPEG_PREFIX}/include")

add_library(essvb_rp SHARED essvb_rp.c essvb_rp.h)
target_include_directories(essvb_rp PRIVATE "${FFMPEG_INCLUDE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(essvb_rp PRIVATE chiaki-lib ${FFMPEG_avcodec} ${FFMPEG_swscale} ${FFMPEG_avutil})

if(WIN32)
    target_link_libraries(essvb_rp PRIVATE ws2_32 wsock32 iphlpapi bcrypt crypt32 secur32 advapi32 user32 shell32 ole32 gdi32 normaliz)
    target_link_options(essvb_rp PRIVATE -static -static-libgcc -Wl,--no-undefined -s)
    set_target_properties(essvb_rp PROPERTIES PREFIX "" C_VISIBILITY_PRESET hidden)
else()
    set_target_properties(essvb_rp PROPERTIES C_VISIBILITY_PRESET hidden)
endif()
