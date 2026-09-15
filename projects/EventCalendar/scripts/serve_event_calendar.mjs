import fs from "node:fs";
import fsp from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { exportEventCalendarData, getDefaultPaths } from "./event_calendar_data.mjs";

const defaults = getDefaultPaths();
const args = process.argv.slice(2);

function readOption(name, fallback) {
  const index = args.indexOf(name);
  if (index >= 0 && args[index + 1]) {
    return args[index + 1];
  }

  return fallback;
}

const host = "127.0.0.1";
const requestedPort = Number(readOption("--port", process.env.PORT || "4173"));
const workbookPath = path.resolve(readOption("--xlsx", process.env.EVENT_CALENDAR_XLSX || defaults.workbookPath));
const outputDir = defaults.outputDir;
const dataPath = path.join(outputDir, "events.json");
const watchedFile = path.basename(workbookPath).toLowerCase();

const mimeTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
  [".png", "image/png"],
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"],
  [".svg", "image/svg+xml"],
]);

let refreshTimer = null;
let refreshInProgress = false;
let refreshQueued = false;

async function refreshData(reason = "manual", attempt = 1) {
  if (refreshInProgress) {
    refreshQueued = true;
    return;
  }

  refreshInProgress = true;
  try {
    const payload = await exportEventCalendarData({ workbookPath, dataPath });
    const stamp = new Date().toLocaleTimeString();
    console.log(`[${stamp}] ${reason}: exported ${payload.eventCount} events`);
  } catch (error) {
    if (attempt < 8) {
      const waitMs = 300 * attempt;
      setTimeout(() => refreshData(`${reason} retry ${attempt}`, attempt + 1), waitMs);
      return;
    }

    console.error(`Could not export events from ${workbookPath}`);
    console.error(error?.stack || error);
  } finally {
    refreshInProgress = false;
    if (refreshQueued) {
      refreshQueued = false;
      refreshData("queued refresh");
    }
  }
}

function scheduleRefresh(reason) {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => refreshData(reason), 450);
}

function safeJoin(root, requestPath) {
  const decodedPath = decodeURIComponent(requestPath.split("?")[0]);
  const relativePath = decodedPath === "/" ? "event-calendar.html" : decodedPath.replace(/^\/+/, "");
  const resolved = path.resolve(root, relativePath);

  if (!resolved.startsWith(path.resolve(root))) {
    return null;
  }

  return resolved;
}

async function handleRequest(request, response) {
  const filePath = safeJoin(outputDir, request.url || "/");
  if (!filePath) {
    response.writeHead(403);
    response.end("Forbidden");
    return;
  }

  try {
    const file = await fsp.readFile(filePath);
    response.writeHead(200, {
      "Content-Type": mimeTypes.get(path.extname(filePath).toLowerCase()) || "application/octet-stream",
      "Cache-Control": "no-store",
    });
    response.end(file);
  } catch {
    response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    response.end("Not found");
  }
}

function listenOnAvailablePort(server, port) {
  return new Promise((resolve, reject) => {
    const tryPort = (candidate) => {
      server.once("error", (error) => {
        if (error.code === "EADDRINUSE" && candidate < port + 20) {
          tryPort(candidate + 1);
          return;
        }

        reject(error);
      });

      server.once("listening", () => resolve(candidate));
      server.listen(candidate, host);
    };

    tryPort(port);
  });
}

await fsp.mkdir(outputDir, { recursive: true });
await refreshData("startup");

fs.watch(path.dirname(workbookPath), (eventType, filename) => {
  if (filename && filename.toLowerCase() === watchedFile) {
    scheduleRefresh(`Excel ${eventType}`);
  }
});

const server = http.createServer(handleRequest);
const activePort = await listenOnAvailablePort(server, requestedPort);
const url = `http://${host}:${activePort}/event-calendar.html`;

console.log("");
console.log(`Event calendar: ${url}`);
console.log(`Watching Excel file: ${workbookPath}`);
console.log("Save the workbook to update the page automatically.");
