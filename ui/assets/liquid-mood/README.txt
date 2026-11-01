LIQUID MOOD — LOCAL IMAGES

Place your own images in these project folders:

  cats/   Cat pictures
  dogs/   Dog pictures
  memes/  Funny memes

Supported formats: JPG, JPEG, JFIF, PNG, GIF, WebP, BMP, AVIF.
Files are read only from these folders. Nothing is downloaded, and no remote
image service is used. SVG files are intentionally not accepted.

The build copies these folders to:
  bin/<Configuration>/net10.0-windows/assets/liquid-mood/

For a packaged application, you can also add images beside the executable under:
  assets/liquid-mood/cats/
  assets/liquid-mood/dogs/
  assets/liquid-mood/memes/

The application creates missing folders at startup. Empty folders are expected
and produce an in-app message rather than an error. Up to 60 images per category
are listed, sorted by filename. Reload a category with "Check again".

Liquid Mood is disabled by default. Enable it with the Liquid Mood switch in
the main preferences panel. "Not now" dismisses it until the next app session.
