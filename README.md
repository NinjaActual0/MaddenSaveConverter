# Madden Save Converter

Bidirectional roster converter between Madden 26 and Madden 27.

## Requirements

- **.NET 10 Desktop Runtime** — [Download](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64)
  - Most Windows 10/11 PCs already have this installed
  - The app is portable — just run the exe, no installation needed

## Usage

### GUI
Run `MaddenSaveConverter.exe` and select source/destination roster files.

### CLI
```
MaddenSaveConverter.exe --convert <source> <destination>
```

### Node.js Scripts (for development)
Requires [madden-file-tools](https://github.com/bep713/madden-file-tools):
```
npm install bep713/madden-file-tools
node convert_m26_to_m27.js <m26 roster> <output path>
node convert_m27_to_m26.js <m27 roster> <m26 template> <output path>
```

## Troubleshooting

> **If your roster is crashing in-game:** After loading the roster, go to the in-game roster editing menu, select **Depth Charts**, and **Auto-Reorder** depth charts for all of your teams. This prevents crashes caused by missing depth chart entries.

## Notes

- File must be named `ROSTER-NAME` (single hyphen) for the game to read it
- All roster files are exactly 6,291,530 bytes (FBCHUNKS format)
