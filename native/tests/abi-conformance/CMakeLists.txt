cmake_minimum_required(VERSION 3.20)
project(abi-conformance C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_executable(abi-conformance main.c)
target_include_directories(abi-conformance PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

if(CMAKE_DL_LIBS)
  target_link_libraries(abi-conformance ${CMAKE_DL_LIBS})
endif()
