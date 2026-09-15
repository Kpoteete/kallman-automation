import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

process.on("uncaughtException", (error) => {
  console.error(error?.stack || error);
  process.exit(1);
});

process.on("unhandledRejection", (error) => {
  console.error(error?.stack || error);
  process.exit(1);
});

const rootDir = process.cwd();
const outputDir = path.join(rootDir, "outputs", "event-calendar-template");
const outputPath = path.join(outputDir, "event-calendar-template.xlsx");

const workbook = Workbook.create();

const schedule = workbook.worksheets.add("Schedule");
const events = workbook.worksheets.add("Events");
const categories = workbook.worksheets.add("Categories");
const locations = workbook.worksheets.add("Locations");

const colors = {
  navy: "#1F4E78",
  blue: "#D9EAF7",
  teal: "#0F766E",
  mint: "#E5F4F1",
  grayText: "#44546A",
  grid: "#D9E2EC",
  body: "#FFFFFF",
  sourceHeader: "#2F5597",
};

const scheduleHeaders = [
  "Event Name",
  "Date",
  "Time",
  "Location",
  "Category",
  "Description",
  "Notes",
  "Required Attendees",
  "Speakers",
];

const eventHeaders = ["Event Name", "Notes"];
const categoryHeaders = ["Category", "Notes"];
const locationHeaders = ["Location", "Notes"];

const eventRows = [
  ["Company Town Hall", "Company-wide meeting"],
  ["Training Session", "Learning and enablement"],
  ["Customer Visit", "External guest or customer event"],
  ["Project Review", "Internal project milestone review"],
  ["Holiday / Office Closure", "Non-working day or closure notice"],
];

const categoryRows = [
  ["Company Event", "Broad employee-facing event"],
  ["Training", "Learning or certification event"],
  ["Customer", "Customer-facing event"],
  ["Operations", "Internal operating cadence"],
  ["Holiday", "Office closure or holiday"],
];

const locationRows = [
  ["Microsoft Teams", "Online meeting"],
  ["Main Conference Room", "Primary office conference room"],
  ["Training Room", "Training or workshop space"],
  ["Auditorium", "Large group venue"],
  ["Offsite", "External location"],
];

function blankRows(count, columns) {
  return Array.from({ length: count }, () => Array(columns).fill(""));
}

function columnLetter(index) {
  let n = index + 1;
  let label = "";
  while (n > 0) {
    const rem = (n - 1) % 26;
    label = String.fromCharCode(65 + rem) + label;
    n = Math.floor((n - 1) / 26);
  }
  return label;
}

function setWidths(sheet, widthsPx, maxRow = 205) {
  widthsPx.forEach((width, index) => {
    const col = columnLetter(index);
    sheet.getRange(`${col}1:${col}${maxRow}`).format.columnWidth = Math.round(width / 7);
  });
}

function applyBaseSheetStyle(sheet, usedRange) {
  sheet.showGridLines = false;
  sheet.getRange(usedRange).format = {
    font: { size: 10, color: "#1F2933" },
    fill: colors.body,
  };
}

function addTitle(sheet, range, title, subtitle) {
  const titleRange = sheet.getRange(range.title);
  titleRange.merge();
  titleRange.values = [[title]];
  titleRange.format = {
    fill: colors.navy,
    font: { bold: true, color: "#FFFFFF", size: 16 },
    horizontalAlignment: "left",
    verticalAlignment: "center",
  };
  titleRange.format.rowHeightPx = 34;

  const subtitleRange = sheet.getRange(range.subtitle);
  subtitleRange.merge();
  subtitleRange.values = [[subtitle]];
  subtitleRange.format = {
    fill: colors.blue,
    font: { color: colors.grayText, italic: true, size: 10 },
    horizontalAlignment: "left",
    verticalAlignment: "center",
  };
  subtitleRange.format.rowHeightPx = 24;
}

function addSourceSheet(sheet, title, subtitle, headers, rows, tableName, widths) {
  applyBaseSheetStyle(sheet, "A1:B204");
  addTitle(sheet, { title: "A1:B1", subtitle: "A2:B2" }, title, subtitle);

  const paddedRows = [...rows, ...blankRows(200 - rows.length, headers.length)];
  sheet.getRange("A4:B204").values = [headers, ...paddedRows];

  const table = sheet.tables.add("A4:B204", true, tableName);
  table.style = "TableStyleMedium2";
  table.showFilterButton = true;

  sheet.getRange("A4:B4").format = {
    fill: colors.sourceHeader,
    font: { bold: true, color: "#FFFFFF" },
    horizontalAlignment: "left",
    verticalAlignment: "center",
  };
  sheet.getRange("A5:B204").format = {
    borders: { preset: "all", style: "thin", color: colors.grid },
    verticalAlignment: "top",
  };
  sheet.getRange("B5:B204").format.wrapText = true;
  sheet.freezePanes.freezeRows(4);
  setWidths(sheet, widths, 204);
}

applyBaseSheetStyle(schedule, "A1:I204");
addTitle(
  schedule,
  { title: "A1:I1", subtitle: "A2:I2" },
  "Event Calendar Schedule",
  "Enter calendar rows here. Dropdown choices come from Events, Categories, and Locations."
);

schedule.getRange("A4:I204").values = [
  scheduleHeaders,
  ...blankRows(200, scheduleHeaders.length),
];

const scheduleTable = schedule.tables.add("A4:I204", true, "ScheduleTable");
scheduleTable.style = "TableStyleMedium2";
scheduleTable.showFilterButton = true;

schedule.getRange("A4:I4").format = {
  fill: colors.teal,
  font: { bold: true, color: "#FFFFFF" },
  horizontalAlignment: "left",
  verticalAlignment: "center",
};
schedule.getRange("A5:I204").format = {
  borders: { preset: "all", style: "thin", color: colors.grid },
  verticalAlignment: "top",
};
schedule.getRange("F5:I204").format.wrapText = true;
schedule.getRange("B5:B204").format.numberFormat = "m/d/yyyy";
schedule.getRange("C5:C204").format.numberFormat = "h:mm AM/PM";
schedule.getRange("A5:E204").format.horizontalAlignment = "left";
schedule.getRange("B5:C204").format.horizontalAlignment = "center";
schedule.freezePanes.freezeRows(4);
setWidths(schedule, [190, 105, 100, 175, 145, 260, 240, 210, 185], 204);

addSourceSheet(
  events,
  "Events",
  "Add or edit selectable event names in the Event Name column.",
  eventHeaders,
  eventRows,
  "EventsTable",
  [230, 340]
);
addSourceSheet(
  categories,
  "Categories",
  "Add or edit schedule categories in the Category column.",
  categoryHeaders,
  categoryRows,
  "CategoriesTable",
  [190, 340]
);
addSourceSheet(
  locations,
  "Locations",
  "Add or edit selectable locations in the Location column.",
  locationHeaders,
  locationRows,
  "LocationsTable",
  [220, 340]
);

schedule.dataValidations.add({
  range: "A5:A204",
  rule: { type: "list", formula1: "Events!$A$5:$A$204" },
});
schedule.dataValidations.add({
  range: "D5:D204",
  rule: { type: "list", formula1: "Locations!$A$5:$A$204" },
});
schedule.dataValidations.add({
  range: "E5:E204",
  rule: { type: "list", formula1: "Categories!$A$5:$A$204" },
});
schedule.dataValidations.add({
  range: "B5:B204",
  rule: {
    type: "date",
    operator: "between",
    formula1: "DATE(2020,1,1)",
    formula2: "DATE(2035,12,31)",
  },
});
schedule.dataValidations.add({
  range: "C5:C204",
  rule: {
    type: "time",
    operator: "between",
    formula1: "TIME(0,0,0)",
    formula2: "TIME(23,59,0)",
  },
});

const schedulePreview = await workbook.inspect({
  kind: "table",
  range: "Schedule!A1:I12",
  include: "values,formulas",
  tableMaxRows: 12,
  tableMaxCols: 9,
  maxChars: 4000,
});
console.log(schedulePreview.ndjson);

const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 300 },
  summary: "final formula error scan",
  maxChars: 2000,
});
console.log(errors.ndjson);

await fs.mkdir(outputDir, { recursive: true });
const xlsx = await SpreadsheetFile.exportXlsx(workbook);
await xlsx.save(outputPath);
console.log(`Saved ${outputPath}`);
