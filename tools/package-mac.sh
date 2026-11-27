#!/bin/bash
# Builds a release package of Cuepoint for Apple Silicon Macs: Cuepoint.app, carrying the .NET runtime and MoltenVK
# (Vulkan on Metal), so nothing else needs installing, signed ad hoc, and zipped with the README and licence as
# artifacts/Cuepoint-<version>-macos-arm64.zip. The version is the project's <Version>, in Cuepoint/Cuepoint.csproj.
#
#     tools/package-mac.sh
#
# Run it on a Mac with the .NET 10 SDK, and MoltenVK and the Vulkan loader from Homebrew:
#
#     brew install molten-vk vulkan-loader
#
# cue-framework must be checked out beside this repository. The GitHub workflow in .github/workflows/release-mac.yml
# runs it for each release.

set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/Cuepoint/Cuepoint.csproj"
runtime=osx-arm64

version="$(dotnet msbuild "$project" -getProperty:Version | tr -d '[:space:]')"
[ -n "$version" ] || { echo "Couldn't read the version from $project" >&2; exit 1; }
name="Cuepoint-$version-macos-arm64"
artifacts="$root/artifacts"
folder="$artifacts/$name"
app="$folder/Cuepoint.app"
zip="$artifacts/$name.zip"

rm -rf "$folder" "$zip"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/vulkan/icd.d" "$app/Contents/Frameworks"

# The app, self-contained, where a Mac app's executable goes. Its shaders go with it, in Shaders.
dotnet publish "$project" -c Release -r "$runtime" -o "$app/Contents/MacOS" -p:DebugType=None

# Vulkan: the loader, and MoltenVK as its driver, described by a file the loader is pointed at (see Program.cs). GLFW
# looks for the loader in Frameworks; .NET looks beside the executable, so it's linked there too.
vulkan_lib="$(brew --prefix vulkan-loader)/lib/libvulkan.1.dylib"
moltenvk_lib="$(brew --prefix molten-vk)/lib/libMoltenVK.dylib"
cp -L "$vulkan_lib" "$app/Contents/Frameworks/libvulkan.1.dylib"
cp -L "$moltenvk_lib" "$app/Contents/Frameworks/libMoltenVK.dylib"
ln -sf ../Frameworks/libvulkan.1.dylib "$app/Contents/MacOS/libvulkan.1.dylib"
cat > "$app/Contents/Resources/vulkan/icd.d/MoltenVK_icd.json" <<'JSON'
{
    "file_format_version": "1.0.0",
    "ICD": {
        "library_path": "../../../Frameworks/libMoltenVK.dylib",
        "api_version": "1.3.0",
        "is_portability_driver": true
    }
}
JSON

cp "$root/Cuepoint/Assets/logo.icns" "$app/Contents/Resources/Cuepoint.icns"

copyright="$(dotnet msbuild "$project" -getProperty:Copyright)"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Cuepoint</string>
    <key>CFBundleDisplayName</key>
    <string>Cuepoint</string>
    <key>CFBundleIdentifier</key>
    <string>com.konajugames.cuepoint</string>
    <key>CFBundleExecutable</key>
    <string>Cuepoint</string>
    <key>CFBundleIconFile</key>
    <string>Cuepoint</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.music</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>Cuepoint mixes your microphone into the main mix when you turn it on.</string>
    <key>NSHumanReadableCopyright</key>
    <string>$copyright</string>
</dict>
</plist>
PLIST

# Apple Silicon runs only signed code; ad hoc signing (no developer certificate) is enough to run it. macOS counts
# everything beside the executable as code, and .NET keeps all its files there (its .dll and .json files, and the
# shaders), so each one is signed, and the libraries in Frameworks, then the app around them. The executable itself is
# left to the app's signing: signing it alone signs the whole app, before the rest are.
find "$app/Contents/MacOS" "$app/Contents/Frameworks" -type f ! -path "$app/Contents/MacOS/Cuepoint" -print0 |
    xargs -0 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"

cp "$root/README.md" "$root/LICENSE" "$folder/"
ditto -c -k --sequesterRsrc --keepParent "$folder" "$zip"
echo "Packaged $zip ($(du -h "$zip" | cut -f1))"
