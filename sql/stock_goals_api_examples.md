# Stock goals API examples

## Create goal

`POST /api/stock/goals`

```json
{
  "termId": 12,
  "goalValue": 2500,
  "validFrom": "2026-04-01",
  "validTo": "2026-04-30"
}
```

Example response:

```json
{
  "id": 5,
  "termId": 12,
  "goalValue": 2500.000,
  "validFrom": "2026-04-01T00:00:00",
  "validTo": "2026-04-30T00:00:00",
  "createdAt": "2026-04-22T08:31:12",
  "updatedAt": "2026-04-22T08:31:12"
}
```

## Get goals for term

`GET /api/stock/goals?termId=12`

Example response (newest first):

```json
[
  {
    "id": 5,
    "termId": 12,
    "goalValue": 2500.000,
    "validFrom": "2026-04-01T00:00:00",
    "validTo": "2026-04-30T00:00:00",
    "createdAt": "2026-04-22T08:31:12",
    "updatedAt": "2026-04-22T08:31:12"
  },
  {
    "id": 4,
    "termId": 12,
    "goalValue": 2300.000,
    "validFrom": "2026-03-01T00:00:00",
    "validTo": "2026-03-31T00:00:00",
    "createdAt": "2026-03-01T06:00:00",
    "updatedAt": "2026-03-01T06:00:00"
  }
]
```
