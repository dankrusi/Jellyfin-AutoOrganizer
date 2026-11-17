#!/bin/sh
# Usage: sh install-synology.sh /path/to/extracted/AutoOrganizer_1.0.2 /path/to/jellyfin/plugins
# Stop Jellyfin in Container Manager before running. This script does not restart services.
set -eu
if [ "$#" -ne 2 ]; then
    echo "Usage: sh $0 EXTRACTED_PLUGIN_FOLDER JELLYFIN_PLUGINS_FOLDER" >&2
    exit 1
fi
source_dir=$1
plugins_dir=$2
if [ ! -f "$source_dir/Jellyfin.Plugin.AutoOrganizer.dll" ] || [ ! -f "$source_dir/AutoOrganizer.Core.dll" ]; then
    echo "Both plugin DLLs must exist in EXTRACTED_PLUGIN_FOLDER." >&2
    exit 1
fi
if [ ! -d "$plugins_dir" ]; then
    echo "JELLYFIN_PLUGINS_FOLDER must already exist. Check the Jellyfin config bind mount." >&2
    exit 1
fi
plugin_name=$(basename "$source_dir")
case "$plugin_name" in
    AutoOrganizer_[0-9]*) ;;
    *) echo "Expected an extracted AutoOrganizer_VERSION folder." >&2; exit 1 ;;
esac
target_dir="$plugins_dir/$plugin_name"
if [ -e "$target_dir" ]; then
    echo "Target already exists: $target_dir. Move the previous installation outside plugins first." >&2
    exit 1
fi
for previous in "$plugins_dir"/*; do
    if [ -f "$previous/Jellyfin.Plugin.AutoOrganizer.dll" ]; then
        echo "An Auto Organizer installation already exists: $previous. Move it outside plugins first." >&2
        exit 1
    fi
done
mkdir "$target_dir"
cp "$source_dir/Jellyfin.Plugin.AutoOrganizer.dll" "$source_dir/AutoOrganizer.Core.dll" "$target_dir/"
chmod 755 "$target_dir"
chmod 644 "$target_dir/Jellyfin.Plugin.AutoOrganizer.dll" "$target_dir/AutoOrganizer.Core.dll"
echo "Installed in $target_dir. Start Jellyfin and open Dashboard > Plugins > Auto Organizer."
