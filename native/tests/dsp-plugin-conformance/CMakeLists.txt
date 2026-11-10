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

# Deliberately wrong fixtures: each must be reported under the check it breaks.
# Every fault is a FIXTURE_FAULT_<NAME> definition of fixture_resampler.c.
set(FIXTURE_FAULTS
  delay:alignment
  floor_length:length
  split:split
  pending_zero:pending
  reset_stale:reset
  process_after_flush:state
  ignore_capacity:capacity
  swap_channels:alignment
  drift:drift
  bad_version:entry
  fail_midstream:runtime
)

foreach(entry IN LISTS FIXTURE_FAULTS)
  string(REPLACE ":" ";" parts "${entry}")
  list(GET parts 0 fault)
  list(GET parts 1 tag)
  string(TOUPPER "${fault}" fault_upper)

  set(target fixture_resampler_${fault})
  add_library(${target} MODULE fixture_resampler.c)
  set_target_properties(${target} PROPERTIES PREFIX "")
  target_include_directories(${target} PRIVATE ${MIRELIA_INCLUDE_DIR})
  target_compile_definitions(${target} PRIVATE FIXTURE_FAULT_${fault_upper})
  if(NOT WIN32)
    target_link_libraries(${target} m)
  endif()

  add_test(NAME fixture_${fault} COMMAND dsp-plugin-conformance $<TARGET_FILE:${target}>)
  set_tests_properties(fixture_${fault} PROPERTIES PASS_REGULAR_EXPRESSION "FAIL \\[${tag}\\]")
endforeach()
