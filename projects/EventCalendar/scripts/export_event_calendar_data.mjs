import path from "node:path";
import { exportEventCalendarData, getDefaultPaths } from "./event_calendar_data.mjs";

const defaults = getDefaultPaths();
const workbookPath = process.argv[2] ? path.resolve(process.argv[2]) : defaults.workbookPath;
const dataPath = process.argv[3] ? path.resolve(process.argv[3]) : defaults.dataPath;

const payload = await exportEventCalendarData({ workbookPath, dataPath });

console.log(`Exported ${payload.eventCount} events`);
console.log(`Source: ${workbookPath}`);
console.log(`JSON: ${dataPath}`);
