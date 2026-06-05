WATS Installer Branding Images
================================

Two BMP files are required for the WiX installer UI branding:

1. wats_dialog_bg.bmp  — 493 x 312 pixels
   Used as the background of the Welcome and Finish dialogs.
   Use the WATS yellow logo image (the one with the bee + WATS + tagline).

2. wats_banner.bmp  — 493 x 58 pixels
   Used as the header banner on all inner installer dialogs.
   Use a cropped/resized version of the plain WATS yellow background.

How to create them:
  - Open the source PNG in Paint / Photoshop / GIMP
  - Resize/crop to the required dimensions
  - Save as 24-bit BMP (no transparency — WiX does not support PNG alpha)

Source images are in:
  src/Converters/DotNet/Public/KohYoung-KSMART_AOI-DB/Assets/
  (Add the WATS logo PNG and background PNG there)

Both files must be present before building the installer with: dotnet build Installer.wixproj
