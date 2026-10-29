/** Display PDF dates without converting their original timezone or inventing missing parts. */
export function formatMetadataDate(value) {
  const match = /^(?:D:)?(\d{4})(?:(\d{2})(?:(\d{2})(?:(\d{2})(?:(\d{2})(?:(\d{2}))?)?)?)?)?(Z|[+-]\d{2}(?:'?\d{2}'?)?)?$/.exec(value);
  if (match) {
    const [, year, month, day, hour, minute, second, zone] = match;
    let result = year;
    if (month) {
      result += '-' + month;
    }
    if (day) {
      result += '-' + day;
    }
    if (hour) {
      result += ' ' + hour;
    }
    if (minute) {
      result += ':' + minute;
    }
    if (second) {
      result += ':' + second;
    }
    if (zone) {
      const digits = zone.replaceAll("'", '');
      result += ' ' + (zone === 'Z' ? '+00:00' : digits.slice(0, 3) + ':' + (digits.slice(3) || '00'));
    }
    return result;
  }
  const iso = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2}(?:\.\d+)?)(Z|[+-]\d{2}:\d{2})$/.exec(value);
  return iso ? `${iso[1]} ${iso[2]} ${iso[3] === 'Z' ? '+00:00' : iso[3]}` : value;
}

/** Changed dates require a complete timestamp and explicit timezone. */
export function metadataDateToIso(value) {
  const iso = value.trim().replace(/^(\d{4}-\d{2}-\d{2})\s+/, '$1T').replace(/\s+(Z|[+-]\d{2}:\d{2})$/, '$1');
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(iso) || !Number.isFinite(Date.parse(iso))) {
    throw new Error('Enter a full date, time and timezone, for example 2026-04-02 11:44:20 +07:00.');
  }
  return iso;
}
