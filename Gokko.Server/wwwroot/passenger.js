"use strict";
const map = document.querySelector("#map");
const panel = document.querySelector("#detail");
const statusBar = document.querySelector("#status");
let selection = null;
let selectedTrain = null;
let detailRequest = 0;
const schedule = document.querySelector("#schedule");
let timer, busy = false;

function node(tag, className, text) {
  const el = document.createElement(tag);
  el.className = className;
  if (text != null) el.textContent = text;
  return el;
}
const kindColors = new Map([
  ["特急", "#bf2027"], ["快急", "#773ab0"], ["快速急行", "#773ab0"],
  ["急行", "#b74d00"], ["区急", "#147444"], ["区間急行", "#147444"],
  ["準急", "#205eb1"], ["普通", "#111111"]
]);
function kindBadge(kind, label) {
  const badge = node("span", "kind", label || kind || "列車");
  badge.style.backgroundColor = kindColors.get(kind) || kindColors.get(label) || "#586472";
  return badge;
}
function trainLabel(number) {
  return String(number).replace(/列車$/, "") + "列車";
}
function showTrainSummary(t, dest, position) {
  show(trainLabel(t.train), "");
  panel.querySelector("p").append(kindBadge(t.kind, t.type), "　" +
    [destination(dest), t.direction, position, t.delay && t.delay + "分遅れ"].filter(Boolean).join("　"));
}
function show(title, message) {
  panel.querySelector("h2").textContent = title;
  panel.querySelector("p").textContent = message;
  if (!panel.open) panel.showModal();
}
function minuteTime(value) {
  return value ? value.split(":").slice(0, 2).join(":") : "―";
}
function destination(value) {
  return value && !/信号場|江原信/.test(value) ? value + "行" : "";
}
async function readBoard(station) {
  try {
    const res = await fetch("/api/passenger/arrivals/" + encodeURIComponent(station.id), {cache:"no-store"});
    if (selection !== station) return;
    if (res.status === 404) { show(station.name, "この駅の接近案内はありません。"); return; }
    if (!res.ok) throw Error();
    const data = await res.json();
    if (selection !== station) return;
    show(station.name, "");
    const summary = panel.querySelector("p");
    for (const board of data.boards) {
      summary.append(board.direction + "\n");
      for (const s of board.stages) {
        const info = [destination(s.dest), s.track && s.track + "番線", s.status].filter(Boolean).join("　");
        summary.append(trainLabel(s.train) + "　", kindBadge(s.kind, s.type), "　" + info + "\n");
      }
      if (!board.stages.length) summary.append("接近列車はありません\n");
      summary.append("\n");
    }
  } catch { if (selection === station) show(station.name, "接近案内を取得できませんでした。"); }
}
async function readTrain(t, position) {
  const request = ++detailRequest;
  const current = () => request === detailRequest && selectedTrain === t.train && panel.open;
  try {
    const res = await fetch("/api/passenger/train?train=" + encodeURIComponent(t.train), {cache:"no-store"});
    if (!res.ok) throw Error();
    const data = await res.json();
    if (!current()) return;
    showTrainSummary(t, data.dest || t.destination, position);
    const cars = data.cars || [];
    const formation = cars.length ? cars.length + "両編成　" + cars.map(c => c || "形式不明").join("・") : "編成情報未取得";
    const table = node("table","");
    const head = table.createTHead().insertRow();
    for (const title of ["停車駅","定刻着","到着見込み"]) head.append(node("th","",title));
    const body = table.createTBody();
    for (const stop of data.stops) {
      const row = body.insertRow();
      for (const value of [stop.name, minuteTime(stop.scheduled), minuteTime(stop.expected)])
        row.append(node("td","",value));
    }
    schedule.replaceChildren(node("strong","",formation),data.stops.length ? table : node("p","","この列車のスタフは未登録です。"));
  } catch {
    if (current()) schedule.textContent = "列車の詳細を取得できませんでした。";
  }
}
panel.addEventListener("close", () => { selection = null; selectedTrain = null; schedule.replaceChildren(); });

function draw({stations, trains, updated}) {
  const rows = [];
  const fragment = document.createDocumentFragment();
  function row(station, label) {
    const el = node("div", station ? "row station" : "row");
    const name = node(station ? "button" : "span", "name");
    if (station) {
      const id = node("span", "station-id");
      const parts = /^(TH)(\d+)$/.exec(station.id);
      if (parts) id.append(node("small", "", parts[1]), node("b", "", parts[2]));
      else id.textContent = station.id;
      const kinds = node("span", "stop-kinds");
      for (const kind of station.kinds || []) kinds.append(kindBadge(kind, kind));
      name.append(id, node("span", "station-name", station.name), kinds);
    }
    if (station) name.onclick = () => {
      selection = station;
      show(label, "接近案内を読み込み中…");
      readBoard(station);
    };
    const up = node("div","lane"), down = node("div","lane");
    el.append(name, up, down); fragment.append(el); rows.push({label, up, down});
  }
  stations.forEach((station,i) => {
    if (i) row(null, stations[i-1].name + "駅〜" + station.name + "駅");
    row(station, station.name + "駅");
  });
  let activeDetail;
  for (const t of trains) {
    const i = stations.findIndex(s => s.zone >= t.zone);
    const at = rows[i * 2 - (stations[i]?.zone === t.zone ? 0 : 1)];
    if (!at) continue;
    const btn = node("button","train");
    const kind = t.type || "列車";
    const tag = kindBadge(t.kind, kind);
    const face = node("img","face");
    face.src = "passenger-train.png";
    face.alt = "";
    const down = t.direction === "下り";
    const number = node("span","number",(down ? "▼ " : "▲ ") + trainLabel(t.train));
    const service = node("span","train-service");
    service.append(tag);
    if (t.delay) service.append(node("span","delay","+" + t.delay + "分"));
    btn.append(service,face,number);
    btn.setAttribute("aria-label", [kind,trainLabel(t.train),t.direction,at.label].join(" "));

    if (selectedTrain === t.train) activeDetail = [t, at.label];
    btn.onclick = () => {
      selection = null;
      selectedTrain = t.train;
      schedule.textContent = "時刻・編成を読み込み中…";
      showTrainSummary(t, t.destination, at.label);
      readTrain(t, at.label);
    };
    (t.direction === "下り" ? at.down : at.up).append(btn);
  }
  if (selectedTrain && panel.open) {
    if (activeDetail) readTrain(...activeDetail);
    else { selectedTrain = null; show("列車情報","この列車の在線情報は終了しました。"); schedule.replaceChildren(); }
  }
  map.replaceChildren(fragment);
  document.querySelector("#range").textContent = stations.length ? stations[0].name + " — " + stations.at(-1).name : "";
  statusBar.textContent = (trains.length ? "在線 " + trains.length + "列車" : "現在、在線列車はありません") + "　｜　最終更新 " + new Date(updated).toLocaleTimeString("ja-JP", {hour:"2-digit", minute:"2-digit", hourCycle:"h23"});
}
async function refresh() {
  clearTimeout(timer);
  if (busy || document.hidden) return;
  busy = true;
  try {
    const res = await fetch("/api/passenger/positions", {cache:"no-store"});
    if (!res.ok) throw Error();
    draw(await res.json());
    if (selection && panel.open && !document.hidden) await readBoard(selection);
  } catch { statusBar.textContent = "在線情報の更新が止まっています"; }
  finally { busy = false; if (!document.hidden) timer = setTimeout(refresh, 5000); }
}
document.addEventListener("visibilitychange", refresh);
refresh();
