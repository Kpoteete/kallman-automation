import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { FileBlob, SpreadsheetFile } from "@oai/artifact-tool";

const MS_PER_DAY = 24 * 60 * 60 * 1000;
const EXCEL_EPOCH_UTC = Date.UTC(1899, 11, 30);

export function getDefaultPaths() {
  const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
  const rootDir = path.resolve(scriptsDir, "..");
  const outputDir = path.join(rootDir, "outputs", "event-calendar-template");

  return {
    rootDir,
    outputDir,
    workbookPath: path.join(outputDir, "event-calendar-template.xlsx"),
    dataPath: path.join(outputDir, "events.json"),
  };
}

function hasValue(value) {
  return value !== null && value !== undefined && String(value).trim() !== "";
}

function textValue(value) {
  if (!hasValue(value)) {
    return "";
  }

  if (value instanceof Date) {
    return value.toISOString();
  }

  return String(value).trim();
}

function pad2(value) {
  return String(value).padStart(2, "0");
}

function excelSerialToDateParts(value) {
  const wholeDays = Math.floor(value);
  const date = new Date(EXCEL_EPOCH_UTC + wholeDays * MS_PER_DAY);

  return {
    year: date.getUTCFullYear(),
    month: date.getUTCMonth() + 1,
    day: date.getUTCDate(),
  };
}

function datePartsToIso(parts) {
  return `${parts.year}-${pad2(parts.month)}-${pad2(parts.day)}`;
}

function labelDate(isoDate) {
  if (!isoDate) {
    return "Date TBD";
  }

  const date = new Date(`${isoDate}T12:00:00Z`);
  return new Intl.DateTimeFormat("en-US", {
    weekday: "short",
    month: "short",
    day: "numeric",
    year: "numeric",
    timeZone: "UTC",
  }).format(date);
}

function parseDateText(value) {
  const trimmed = value.trim();
  const isoMatch = trimmed.match(/^(\d{4})-(\d{1,2})-(\d{1,2})$/);
  if (isoMatch) {
    return {
      year: Number(isoMatch[1]),
      month: Number(isoMatch[2]),
      day: Number(isoMatch[3]),
    };
  }

  const usMatch = trimmed.match(/^(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})$/);
  if (usMatch) {
    const shortYear = Number(usMatch[3]);
    return {
      year: shortYear < 100 ? 2000 + shortYear : shortYear,
      month: Number(usMatch[1]),
      day: Number(usMatch[2]),
    };
  }

  const parsed = new Date(trimmed);
  if (!Number.isNaN(parsed.getTime())) {
    return {
      year: parsed.getUTCFullYear(),
      month: parsed.getUTCMonth() + 1,
      day: parsed.getUTCDate(),
    };
  }

  return null;
}

function parseDateCell(value) {
  if (!hasValue(value)) {
    return { date: "", dateLabel: "Date TBD", dateSort: "9999-12-31" };
  }

  let parts = null;
  if (value instanceof Date) {
    parts = {
      year: value.getUTCFullYear(),
      month: value.getUTCMonth() + 1,
      day: value.getUTCDate(),
    };
  } else if (typeof value === "number" && Number.isFinite(value)) {
    parts = excelSerialToDateParts(value);
  } else {
    parts = parseDateText(String(value));
  }

  if (!parts || !parts.year || !parts.month || !parts.day) {
    const fallback = textValue(value);
    return { date: "", dateLabel: fallback || "Date TBD", dateSort: "9999-12-31" };
  }

  const date = datePartsToIso(parts);
  return { date, dateLabel: labelDate(date), dateSort: date };
}

function minutesToTime(minutes) {
  const normalized = ((minutes % 1440) + 1440) % 1440;
  const hour24 = Math.floor(normalized / 60);
  const minute = normalized % 60;
  const suffix = hour24 >= 12 ? "PM" : "AM";
  const hour12 = hour24 % 12 || 12;

  return {
    time: `${pad2(hour24)}:${pad2(minute)}`,
    timeLabel: `${hour12}:${pad2(minute)} ${suffix}`,
    timeSort: `${pad2(hour24)}:${pad2(minute)}`,
  };
}

function parseTimeText(value) {
  const trimmed = value.trim();
  const match = trimmed.match(/^(\d{1,2})(?::(\d{2}))?\s*([ap]\.?m\.?)?$/i);
  if (!match) {
    return null;
  }

  let hour = Number(match[1]);
  const minute = Number(match[2] || "0");
  const meridiem = match[3]?.toLowerCase().replaceAll(".", "");

  if (meridiem === "pm" && hour < 12) {
    hour += 12;
  }

  if (meridiem === "am" && hour === 12) {
    hour = 0;
  }

  if (hour > 23 || minute > 59) {
    return null;
  }

  return hour * 60 + minute;
}

function parseTimeCell(value) {
  if (!hasValue(value)) {
    return { time: "", timeLabel: "Time TBD", timeSort: "99:99" };
  }

  let minutes = null;
  if (value instanceof Date) {
    minutes = value.getUTCHours() * 60 + value.getUTCMinutes();
  } else if (typeof value === "number" && Number.isFinite(value)) {
    const fraction = ((value % 1) + 1) % 1;
    minutes = Math.round(fraction * 1440);
  } else {
    minutes = parseTimeText(String(value));
  }

  if (minutes === null) {
    const fallback = textValue(value);
    return { time: "", timeLabel: fallback || "Time TBD", timeSort: "99:99" };
  }

  return minutesToTime(minutes);
}

function readFirstColumn(workbook, sheetName, rangeAddress) {
  try {
    const sheet = workbook.worksheets.getItem(sheetName);
    return sheet
      .getRange(rangeAddress)
      .values.flat()
      .map(textValue)
      .filter(Boolean);
  } catch {
    return [];
  }
}

function uniqueSorted(values) {
  return [...new Set(values.map(textValue).filter(Boolean))].sort((a, b) =>
    a.localeCompare(b, undefined, { sensitivity: "base" })
  );
}

function rowHasContent(row) {
  return row.some(hasValue);
}

export async function exportEventCalendarData({
  workbookPath = getDefaultPaths().workbookPath,
  dataPath = getDefaultPaths().dataPath,
} = {}) {
  const input = await FileBlob.load(workbookPath);
  const workbook = await SpreadsheetFile.importXlsx(input);
  const schedule = workbook.worksheets.getItem("Schedule");
  const rows = schedule.getRange("A5:I204").values.filter(rowHasContent);

  const events = rows.map((row, index) => {
    const [eventName, dateCell, timeCell, location, category, description, notes, requiredAttendees, speakers] = row;
    const date = parseDateCell(dateCell);
    const time = parseTimeCell(timeCell);
    const title = textValue(eventName) || "Untitled Event";

    return {
      id: `${date.dateSort}-${time.timeSort}-${index + 1}`,
      title,
      date: date.date,
      dateLabel: date.dateLabel,
      time: time.time,
      timeLabel: time.timeLabel,
      location: textValue(location),
      category: textValue(category),
      description: textValue(description),
      notes: textValue(notes),
      requiredAttendees: textValue(requiredAttendees),
      speakers: textValue(speakers),
      sortKey: `${date.dateSort}T${time.timeSort}`,
    };
  });

  events.sort((a, b) => a.sortKey.localeCompare(b.sortKey) || a.title.localeCompare(b.title));

  const categories = uniqueSorted([
    ...readFirstColumn(workbook, "Categories", "A5:A204"),
    ...events.map((event) => event.category),
  ]);
  const locations = uniqueSorted([
    ...readFirstColumn(workbook, "Locations", "A5:A204"),
    ...events.map((event) => event.location),
  ]);

  const payload = {
    generatedAt: new Date().toISOString(),
    source: path.basename(workbookPath),
    eventCount: events.length,
    categories,
    locations,
    events,
  };

  await fs.mkdir(path.dirname(dataPath), { recursive: true });
  await fs.writeFile(dataPath, `${JSON.stringify(payload, null, 2)}\n`, "utf8");

  return payload;
}
