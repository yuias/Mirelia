cmake_minimum_required(VERSION 3.20)
project(dsp-plugin-conformance C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# The drift check converts ten million frames; it is too slow without optimisation.
if(NOT CMAKE_CONFIGURATION_TYPES AND NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

enable_testing()

set(MIRELIA_INCLUDE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

add_executable(dsp-plugin-conformance main.c)
target_include_directories(dsp-plugin-conformance PRIVATE ${MIRELIA_INCLUDE_DIR})
if(CMAKE_DL_LIBS)
  target_link_libraries(dsp-plugin-conformance ${CMAKE_DL_LIBS})
endif()
if(NOT WIN32)
  target_link_libraries(dsp-plugin-conformance m)
endif()

add_library(fixture_resampler_good MODULE fixture_resampler.c)
set_target_properties(fixture_resampler_good PROPERTIES PREFIX "")
target_include_directories(fixture_resampler_good PRIVATE ${MIRELIA_INCLUDE_DIR})
if(NOT WIN32)
  target_link_libraries(fixture_resampler_good m)
endif()

add_test(NAME fixture_good COMMAND dsp-plugin-conformance $<TARGET_FILE:fixture_resampler_good>)
