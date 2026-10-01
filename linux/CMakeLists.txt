cmake_minimum_required(VERSION 3.21)
project(spicy-wallpaper VERSION 0.1 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

include(GNUInstallDirs)

find_package(Qt6 6.5 REQUIRED COMPONENTS Core Gui Widgets Network WebSockets DBus Qml Quick WebEngineCore WebEngineQuick)
find_package(LayerShellQt REQUIRED)

add_executable(spicy-wallpaper
  src/main.cpp
  src/Common.h src/Common.cpp
  src/Config.h src/Config.cpp
  src/Matching.h src/Matching.cpp
  src/Lyrics.h src/Lyrics.cpp
  src/BridgeServer.h src/BridgeServer.cpp
  src/MprisWatcher.h src/MprisWatcher.cpp
  src/LyricsService.h src/LyricsService.cpp
  src/WallpaperFeed.h src/WallpaperFeed.cpp
  src/LockWatcher.h src/LockWatcher.cpp
  src/WallpaperWindows.h src/WallpaperWindows.cpp
  src/Tray.h src/Tray.cpp
)

target_link_libraries(spicy-wallpaper PRIVATE
  Qt6::Core Qt6::Gui Qt6::Widgets Qt6::Network Qt6::WebSockets Qt6::DBus
  Qt6::Qml Qt6::Quick Qt6::WebEngineCore Qt6::WebEngineQuick
  LayerShellQt::Interface)

# The page is looked up at runtime: config "WallpaperDir" -> <prefix>/share/... -> this checkout.
target_compile_definitions(spicy-wallpaper PRIVATE
  SPICY_SOURCE_WALLPAPER_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../wallpaper"
  SPICY_INSTALL_WALLPAPER_DIR="${CMAKE_INSTALL_FULL_DATADIR}/spicy-wallpaper/wallpaper")

install(TARGETS spicy-wallpaper RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
