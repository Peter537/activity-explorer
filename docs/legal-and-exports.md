# Legal and source-export guides

Activity Explorer uses a file-first model so the data owner explicitly requests and supplies their own export. It does not use unofficial Garmin libraries, Garmin Connect automation, Strava OAuth, browser scraping, or stored account credentials.

## Official data-export paths

- [Garmin: Exporting data from Garmin Connect](https://support.garmin.com/en-US/?faq=W1TvTPW8JZ6LfJSfK512Q8)
- [Strava: Exporting your data and bulk export](https://support.strava.com/en-us/articles/15401919-exporting-your-data-and-bulk-export)

Follow the practical walkthroughs for [Garmin account exports](imports.md#garmin-account-exports) and [Strava bulk exports](imports.md#strava-bulk-exports) to request an archive and upload its outer ZIP directly to Activity Explorer.

Provider interfaces and export layouts can change. Follow the provider's current instructions, request only data you are entitled to receive, and retain the original downloaded archive.

## API position

Activity Explorer 0.1.0 intentionally has no Garmin or Strava API integration.

Garmin describes the Garmin Connect Developer Program as a business-focused program. Activity Explorer does not enroll, impersonate a business integration, use an unofficial Connect client, accept Garmin credentials, or automate Connect pages:

- [Garmin Connect Developer Program FAQ](https://developer.garmin.com/gc-developer-program/program-faq/)
- [Garmin Terms of Use](https://www.garmin.com/en-US/legal/terms-of-use/)

Activities reaching Garmin through a watch or phone do not create a supported personal integration path for this app. Activity Explorer therefore has no supported automatic personal Garmin Connect sync in 0.1.0; users initiate account exports or individual file exports themselves. A future direct integration would require official Developer Program approval and OAuth 2.0 and would be a separate feature.

Strava's developer program, subscription eligibility, and API agreement can change, and its platform policies govern storage and display of API-derived data. The application therefore does not create or update a Strava API application:

- [Strava developer-program announcement](https://communityhub.strava.com/insider-journal-9/an-update-to-our-developer-program-13428)
- [Strava API agreement](https://www.strava.com/legal/api)

An official account export is different from automated API collection: the account holder initiates a documented portability/download process and supplies the resulting files locally.

## Local segments

Activity Explorer does not import, mirror, or present Strava's proprietary segment catalog. A local smart segment is a user-owned path definition created from the user's own activity/route data. Efforts are calculated only from files assigned to that local owner profile.

Do not publish segment or activity data that reveals another person's private location without their permission.

## FIT SDK

FIT parsing uses Garmin's official C# FIT SDK distributed through NuGet:

- [Garmin: Get the FIT SDK](https://developer.garmin.com/fit/get-the-sdk/)
- [Garmin.FIT.Sdk on NuGet](https://www.nuget.org/packages/Garmin.FIT.Sdk/21.212.0)

The package is subject to Garmin's FIT Protocol License, not this repository's MIT license. The SDK is not copied into this repository. Review [Dependency and license inventory](dependencies.md) before building or redistributing the project.

## OpenStreetMap and maps

The configured online basemap uses OpenStreetMap-derived sources and must retain visible attribution:

- [OpenStreetMap copyright and license](https://www.openstreetmap.org/copyright)
- [OpenFreeMap](https://openfreemap.org/)

The online provider receives normal tile/style requests for the current map area. Blank-basemap mode avoids those map requests.

## Disclaimers

Activity Explorer is unofficial and is not affiliated with, endorsed by, sponsored by, or supported by Garmin, Strava, OpenFreeMap, or OpenStreetMap.

Garmin, Strava, OpenFreeMap, OpenStreetMap, and other product names belong to their respective owners. Names are used descriptively to identify file formats, export sources, and map attribution. No third-party logos or credentials are distributed.

The source code is provided under the MIT License without warranty. This guide documents the project's technical choices and is not legal advice.
