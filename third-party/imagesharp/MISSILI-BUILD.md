# ImageSharp source build for Missili

This package is a Missili-maintained source build, not an official Six Labors package.

- Package: `Missili.ThirdParty.ImageSharp` version `4.1.3-missili.2`.
- ImageSharp: tag `v4.1.3`, commit `751037d0c6384c97e77ee1abd86e2203ae8dbd95`.
- SharedInfrastructure: commit `7804b206c2c41b07b0e617a6c1aa4d97a212bbed`.
- Branding submodule: commit `c939db5ac22e0fbafcf535f1312b53d889c727ca`.
- Runtime assembly: `SixLabors.ImageSharp`, version `4.1.3.0`, target `net8.0`.

## Applicable license

Copyright (c) Six Labors and contributors. This source build is used in the AGPL-3.0 Missili open-source project under the Apache-2.0 grant specified in the upstream LICENSE. The [NuGet package](../nuget/Missili.ThirdParty.ImageSharp.4.1.3-missili.2.nupkg) contains `LICENSE`, `LICENSE-APACHE-2.0.txt`, `THIRD-PARTY-NOTICES.TXT`, and `shared-infrastructure/THIRD-PARTY-NOTICES.TXT`. Missili's project license does not replace the licenses of these components.

## Build modifications

The Missili build removes the `SixLabors.Licensing` development dependency and the targets that inject its validation task into packages. It uses a distinct package identity, includes license texts and source provenance, and omits the optional branding icon.

Version `4.1.3-missili.2` also fixes `PngFrameMetadata.FromChunk`: an APNG frame delay denominator of zero means 100 under the PNG specification. The denominator is normalized before constructing a rational value, preserving the numerator and frame duration. Existing copyright notices remain intact.

The shared infrastructure build files contain modification notices. The Missili repository retains the corresponding patches in `third-party/imagesharp`. This package is supplied through the repository-local NuGet feed.

## Sources

- https://github.com/SixLabors/ImageSharp/tree/v4.1.3
- https://github.com/SixLabors/SharedInfrastructure/tree/7804b206c2c41b07b0e617a6c1aa4d97a212bbed
- https://www.apache.org/licenses/LICENSE-2.0
- https://www.w3.org/TR/png/#11fcTL
