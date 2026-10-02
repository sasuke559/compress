-- Anonymous counters only: no IDs, no IP addresses, no file names.
CREATE TABLE IF NOT EXISTS counts (
  day     TEXT NOT NULL,  -- UTC date, YYYY-MM-DD
  event   TEXT NOT NULL,  -- install | active | export
  detail  TEXT NOT NULL,  -- app version (install/active) or tool name (export)
  n       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (day, event, detail)
);
