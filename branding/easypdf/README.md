# Brand "easypdf": EasyPDF

The default brand (also the template for new brands, see [../README.md](../README.md)).

**Release repository:** the folder `EasyPDF` next to the development repository
([docs/BUILDING.md](../../docs/BUILDING.md), "Development and release repositories").

**Look:** the header runs from the darkest blue of the UBX company logo (`logos/company/`), `#0061AC`
(`BrandHeaderColor`), into the darker `#003A66` (`BrandPrimaryColor`: tab band, selection, icons, tinted greys); the
light blue `#79A5D7` of the logo is the signal colour (line under the band, Save button). `#003A66` is the lightest blue
of that hue whose text on the light blue still has a contrast of 4.5:1. Other colour schemes:
[../Color variant examples](../Color%20variant%20examples/README.md).

**Icon:** the draft `logos/application (drafts)/icon_E.svg` (two pages, plus sign, ".PDF" label), rendered as a
1024 px PNG and made into the icon with `make_icon.py --brand easypdf --logo <png>`; it is kept as `icons/logo.png` and
also replaces the drawn mark in the header (`icons/header-logo.png`). Deleting `icons/logo.png` and running
`make_icon.py --brand easypdf` goes back to the drawn mark.

**Publisher:** UBX GmbH (publisher and copyright line).

**Logos:** `logos/company/` holds the UBX company logo as print file (PNG) and as SVG traced from it
(`trace_logo.py "logos/company/UBX-farbig ohne Unterzeile 30.000x13.547.png" logos/company/UBX-Logo.svg --corner 150`);
`icons/company-logo.svg`, shown in Help ▸ About, is made from the SVG
(`make_icon.py --brand easypdf --company-logo branding/easypdf/logos/company/UBX-Logo.svg`). `logos/application (drafts)/` holds logo drafts for EasyPDF, not used
by the build (`icon_E.svg` is the current icon); a draft can become the program icon with `make_icon.py --brand easypdf --logo <file>` (crop the symbol
without the lettering first).
