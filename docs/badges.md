# Badges and levels

Open **Badges** to see achievements calculated from your imported activity history. Qualification is automatic: no enrolment, Garmin account connection, or purchase is required. Cycling, running, walking, and rowing count, including indoor and virtual activities.

## Browse your history

Choose a profile to open its collection. **All profiles** shows separate point and level summaries; it never combines people's achievements.

**By month** shows the selected month's challenges and special dates, overlapping quarterly/yearly challenges, and lifetime progress. April 2026 shows history through April 30, 2026; a May activity cannot complete an April snapshot. The current month uses recorded history through now. Future months preview their challenges without counting future-dated activities.

**Collection** groups related tiers and editions into families. Each family shows its latest applicable edition and how many badges have been earned across the family. Sport, type, status, and search filters narrow the results; pages contain up to 24 badges or families. URLs preserve the profile, view, month, filters, and page through refresh and browser Back/Forward.

Collections remain browsable without activities. If the available history contains no leap year, the Leap Day family previews its next edition.

Open a badge for its exact requirement, points, qualifying date, supporting activities, and related tiers and editions. For example, the New Year's Eve badges for 2024 and 2025 are separate awards in the same family. Expand a year to inspect its earned and missed editions.

| Status | Meaning at the selected snapshot |
| --- | --- |
| Completed | The requirement has been met. |
| In progress | Some progress exists and the qualifying period remains open, or the badge is a lifetime milestone. |
| Incomplete | Some progress exists, but the qualifying period has ended. |
| Not started | No qualifying progress has been recorded. |
| Upcoming | The qualifying period has not started. |

Historical incomplete and not-started editions can still be completed by importing activities recorded during those periods. Choose **Refresh** after importing or editing data in another tab. Loading failures offer retry; an invalid profile timezone directs you to Profiles.

## Qualification rules

Set **Badge timezone** in Profiles. The default is **Europe/Copenhagen**. Dates and morning/night windows use this timezone with its historical daylight-saving rules, independent of the browser timezone and source-file offset. Changing it recalculates historical awards. The whole activity belongs to its local start date, even if it ends after midnight; its distance and time are not split across days.

- Distance and ascent use finite, positive recorded summary values. Distances are in kilometres; ascent is in metres. Thresholds use the recorded values before display rounding.
- Moving-time badges use recorded moving time. Values marked unavailable never count; elapsed time is not substituted.
- Activity counts, sport variety, special dates, Early Bird, and Night Owl require an activity with at least **10 moving minutes**.
- An **active day** needs **20 total moving minutes**, summed across that day's supported activities.
- Weekly consistency needs three active days in each consecutive Monday–Sunday week. It awards on the third active day in the required final week. Progress shows the best qualifying run, including runs across years.
- Single-activity milestones award once, on the earliest qualifying activity's local date. Other lifetime milestones also award once per tier.
- Monthly, quarterly, annual, and special-date editions award once each. Each qualifying lower tier earns its own points: cycling 400 km in a month earns the 100, 200, and 400 km badges for **7 points** altogether.
- Activity identity comes from the existing import deduplication. Reimporting the same source or adding equivalent source provenance does not award another copy.

There is no launch-year cutoff: an activity recorded in 2018 can earn its 2018 awards when imported in 2026. These are local Activity Explorer achievements, not imported Garmin awards. Their rules apply retrospectively, including commemorative dates before this application existed.

## Calendar and clock badges

Each annual edition awards one point. Early Bird starts at or after **04:00** and before **07:00**; Night Owl starts at or after **22:00** or before **04:00**. Starting outside the window does not qualify even if the activity continues into it.

| Special date | Day | Sport |
| --- | --- | --- |
| New Year's Day | January 1 | Any supported sport |
| New Year's Eve | December 31 | Any supported sport |
| Leap Day | February 29, in leap years | Any supported sport |
| Earth Day | April 22 | Any supported sport |
| World Bicycle Day | June 3 | Cycling |
| World Environment Day | June 5 | Any supported sport |

The international dates follow the United Nations pages for [Earth Day](https://www.un.org/en/observances/earth-day/background), [World Bicycle Day](https://www.un.org/en/observances/bicycle-day/), and [World Environment Day](https://www.un.org/en/observances/environment-day/).

All challenges use whole calendar months, quarters, years, or the listed dates. There are no random weekend windows, sensor/HR-zone rules, weather or wellness challenges, branded events, or custom challenge editor.

## Catalogue reference

Catalogue version **1** has **134 definitions in 45 families**. Each slash-separated target pairs with the point value in the same position. The family identifier specifies the sport, period, and measurement. `all` means a shared achievement. This table is checked against the executable catalogue by the test suite.

| Family | Targets | Unit | Points |
| --- | --- | --- | --- |
| all-annual-morning | 1 | activity | 1 |
| all-annual-night | 1 | activity | 1 |
| all-lifetime-consistentweeks | 4 / 12 / 26 | weeks | 2 / 4 / 8 |
| all-monthly-activedays | 5 / 10 / 20 | days | 1 / 2 / 4 |
| all-monthly-variety | 3 / 4 | sports | 2 / 4 |
| bicycle-day | 1 | activity | 1 |
| cycling-annual-distance | 4000 | km | 8 |
| cycling-lifetime-activitycount | 1 / 25 / 100 / 500 / 1000 | activities | 1 / 2 / 4 / 8 / 16 |
| cycling-lifetime-distance | 1000 / 5000 / 10000 / 25000 | km | 2 / 4 / 8 / 16 |
| cycling-lifetime-singleascent | 100 / 250 / 500 / 1000 / 2000 | m | 1 / 2 / 4 / 8 / 16 |
| cycling-lifetime-singledistance | 5 / 10 / 25 / 50 / 100 / 150 / 200 | km | 1 / 1 / 2 / 4 / 8 / 8 / 16 |
| cycling-monthly-ascent | 1000 / 5000 / 10000 | m | 1 / 2 / 4 |
| cycling-monthly-distance | 100 / 200 / 400 / 800 | km | 1 / 2 / 4 / 8 |
| cycling-monthly-movingtime | 5 / 10 / 20 | hours | 1 / 2 / 4 |
| cycling-quarterly-distance | 1000 | km | 4 |
| earth-day | 1 | activity | 1 |
| environment-day | 1 | activity | 1 |
| leap-day | 1 | activity | 1 |
| new-year | 1 | activity | 1 |
| rowing-annual-distance | 600 | km | 8 |
| rowing-lifetime-activitycount | 1 / 25 / 100 / 500 / 1000 | activities | 1 / 2 / 4 / 8 / 16 |
| rowing-lifetime-distance | 100 / 500 / 1000 / 5000 | km | 2 / 4 / 8 / 16 |
| rowing-lifetime-singledistance | 1 / 2 / 5 / 10 / 21.0975 / 42.195 | km | 1 / 1 / 2 / 4 / 8 / 16 |
| rowing-monthly-distance | 10 / 25 / 50 / 100 | km | 1 / 2 / 4 / 8 |
| rowing-monthly-movingtime | 5 / 10 / 20 | hours | 1 / 2 / 4 |
| rowing-quarterly-distance | 150 | km | 4 |
| running-annual-distance | 1000 | km | 8 |
| running-lifetime-activitycount | 1 / 25 / 100 / 500 / 1000 | activities | 1 / 2 / 4 / 8 / 16 |
| running-lifetime-distance | 100 / 500 / 1000 / 5000 | km | 2 / 4 / 8 / 16 |
| running-lifetime-singleascent | 100 / 250 / 500 / 1000 | m | 1 / 2 / 4 / 8 |
| running-lifetime-singledistance | 1 / 5 / 10 / 21.0975 / 42.195 | km | 1 / 2 / 4 / 8 / 16 |
| running-monthly-ascent | 250 / 1000 / 2000 | m | 1 / 2 / 4 |
| running-monthly-distance | 20 / 50 / 100 / 200 | km | 1 / 2 / 4 / 8 |
| running-monthly-movingtime | 5 / 10 / 20 | hours | 1 / 2 / 4 |
| running-quarterly-distance | 250 | km | 4 |
| walking-annual-distance | 600 | km | 8 |
| walking-lifetime-activitycount | 1 / 25 / 100 / 500 / 1000 | activities | 1 / 2 / 4 / 8 / 16 |
| walking-lifetime-distance | 100 / 500 / 1000 / 5000 | km | 2 / 4 / 8 / 16 |
| walking-lifetime-singleascent | 100 / 250 / 500 / 1000 | m | 1 / 2 / 4 / 8 |
| walking-lifetime-singledistance | 1 / 5 / 10 / 20 / 30 / 50 | km | 1 / 1 / 2 / 4 / 8 / 16 |
| walking-monthly-ascent | 250 / 1000 / 2000 | m | 1 / 2 / 4 |
| walking-monthly-distance | 10 / 25 / 50 / 100 | km | 1 / 2 / 4 / 8 |
| walking-monthly-movingtime | 5 / 10 / 20 | hours | 1 / 2 / 4 |
| walking-quarterly-distance | 150 | km | 4 |
| year-end | 1 | activity | 1 |

## Points and levels

Level **L** begins at **5 × (L − 1)²** total points. Level 1 starts at zero; there is no configured level cap. The next level requires **10L − 5** additional points. Points are not spent when a level is reached.

| Level | Total points |
| --- | --- |
| 5 | 80 |
| 10 | 405 |
| 15 | 980 |
| 20 | 1805 |
| 25 | 2880 |

The evaluator's fictional full-year balance tests produce 28 recurring points for one weekly 25 km ride, 260 for three weekly 50 km rides, 408 for five weekly 50 km rides, 192 for four weekly 5 km walks, and 156 for three weekly 5 km rows. These scenarios exclude first-time milestones and special dates; their exact moving times and ascent are specified in `BadgeTests`. They illustrate the progression, not a forecast for an individual.

## Corrections and storage

Badges are derived from the current activity summaries when requested. No award ledger, new database table, background badge worker, external artwork request, or GPS-stream decoding is needed. Importing an earlier qualifying activity can move a milestone's earned date earlier. Deleting, correcting, or transferring supporting activities can remove badges and reduce points or levels. Profile transfers recalculate each collection independently.

The profile JSON export includes `profile.timeZoneId`; awards can be recalculated from the underlying history and catalogue. The export remains a summary, not a complete backup. See [Data storage and privacy](data-storage-and-privacy.md).
