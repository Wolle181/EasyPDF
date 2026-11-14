# Brand "inter": SmartPDF

A customization of the app: **SmartPDF** for the INTER Versicherungsgruppe. Build it with
`-p:Brand=inter` or `EASYPDF_BRAND=inter` (see [../README.md](../README.md)).

**Look:** sober corporate design after the INTER CI (by wirDesign) and a sample app called SmartPDF: dark navy `#001B41`
as primary colour, the yellow `#DEDC00` as the only signal colour, white and cool greys, typeface Barlow. The round ribbon
buttons follow that sample app.

**Icon:** the drawn brand mark in navy and yellow (`python tools/icon/make_icon.py --brand inter`).

**Company logo** in Help ▸ About: `icons/company-logo.svg`, made from `logos/INTER-Logo.svg`
(`make_icon.py --brand inter --company-logo branding/inter/logos/INTER-Logo.svg`), which is traced from
`logos/INTER-Logo.png` (`trace_logo.py branding/inter/logos/INTER-Logo.png branding/inter/logos/INTER-Logo.svg`).
`logos/INTER_Logo.webp` is another picture of the logo with a wider crop.

**Ids:** `MsiUpgradeCode` 7E0D8C5B-… and `MacBundleId` are fixed; never change them.
