cmake_minimum_required(VERSION 3.20)

# The runtime must be set before the target is created. Static linking keeps the
# plugin dependent on system DLLs only, so it loads without a Visual C++
# redistributable next to the application.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

project(mirelia_r8brain CXX)

if(NOT WIN32)
  message(FATAL_ERROR "The r8brain resampler plugin is not ported to macOS yet.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

# r8brain-free-src 7.7, pinned by commit so every build compiles the source the
# conformance suite was run against. Upstream has no CMake project; the fetch
# only puts the headers on disk.
include(FetchContent)
FetchContent_Declare(r8brain
  GIT_REPOSITORY https://github.com/avaneev/r8brain-free-src.git
  GIT_TAG        9ca64dacf35ef633d7648cfaa8e82fd0ca8e41d6)
FetchContent_MakeAvailable(r8brain)

# r8brain is header-only, so the plugin is a single translation unit.
add_library(mirelia_r8brain MODULE src/r8brain_resampler.cpp)
set_target_properties(mirelia_r8brain PROPERTIES PREFIX "")
target_include_directories(mirelia_r8brain PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}/../../include
  ${r8brain_SOURCE_DIR})
target_compile_options(mirelia_r8brain PRIVATE
  $<$<CXX_COMPILER_FRONTEND_VARIANT:GNU>:-Wall -Wextra>)
