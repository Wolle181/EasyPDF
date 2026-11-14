# Color variant examples

Alternative colour schemes for the default brand ([../easypdf](../easypdf/README.md)), as a reference for a new brand or
a change of look. Only the colours differ; everything else (name, icon, logos, publisher) is as in `easypdf`. This folder is no brand profile (it has no `Branding.props`), the build ignores it.

The colours are the values of a brand profile (`branding/<id>/Branding.props`, see [../README.md](../README.md)):
**primary** (header and tab band, selection, icons; its hue tints the greys), **accent** (line under the band, Save
button) and the optional **header** colour (top of the header gradient; without it the header starts in a slightly
lighter primary). Contrast: text in the primary colour on the accent (Save button), at least 4.5:1 is good.

| Scheme | Primary | Accent | Header top | Contrast | Screenshot |
|---|---|---|---|---|---|
| Dark blue with light blue line (**look of `easypdf`**) | `#003A66` | `#79A5D7` | `#0061AC` | 4.5:1 | – |
| Grey with blue line | `#323232` | `#79A5D7` | – | 5.0:1 | [Gray-with-blue-line.png](Gray-with-blue-line.png) |
| Night blue with amber line | `#14213D` | `#F2A541` | – | 7.8:1 | [Darkbblue-with-orange-line.png](Darkbblue-with-orange-line.png) |
| Petrol with mint line | `#103B40` | `#6FD3BF` | – | 6.8:1 | [Green.png](Green.png) |

Notes:
- **Dark blue:** the header runs from the darkest blue of the company logo into a darker blue of the same hue; `#003A66`
  is the lightest blue of that hue that still reaches 4.5:1 on the light blue. `#00457A` looks a little lighter but
  only reaches 3.8:1.
- **Grey:** anthracite and light blue, both from the company logo; sober and neutral.
- **Night blue / amber:** calm and serious, with one warm signal colour.
- **Petrol / mint:** modern and fresh.
- The screenshots keep the program icon in the light blue of `easypdf`; for an amber or mint look it would be drawn in
  that colour (`tools/icon/make_icon.py`).

## Trying a scheme

Without changing any file, pass the colours to the build, e.g. for the screenshots of both themes:

```
EASYPDF_SCREENSHOTS=<folder> dotnet test tests/EasyPDF.App.Tests --filter ScreenshotTests "-p:BrandPrimaryColor=#14213D" "-p:BrandAccentColor=#F2A541"
dotnet run --project src/EasyPDF.App "-p:BrandPrimaryColor=#14213D" "-p:BrandAccentColor=#F2A541"
```

A scheme of the table has no header colour of its own; when trying it on top of `easypdf`, which sets one, also pass
`"-p:BrandHeaderColor="` (empty). To install a scheme next to the app, make it a brand profile of its own (copy
`easypdf/`, new name and installer ids, see [../README.md](../README.md)).
