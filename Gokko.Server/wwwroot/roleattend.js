// 司令員・信号扱いの出勤（乗務メニュー menu.html・乗務タブレット tablet.html で使う）
// ・GokkoRoleAttend.guard(role, token, go): その役職の画面を開く前に、出勤していなければ「出勤」のポップを出す。出勤したら go(駅) で開く
//   信号扱いは駅ごと（同じ駅に何人でもよい）。ポップで扱う駅を選ぶ。出勤していれば、その駅で開く（go に駅 ID を渡す。司令員は空）
// ・GokkoRoleAttend.panel(el, getToken): 出勤者の欄（役職ごとの名前。信号扱いは駅ごとの枠。自分の名前の横に［退勤］）。10秒ごとに読み直す
// 乗務の出勤（行路）とは別。サーバーは /api/tid/crew/role-attendance・/api/tid/crew/role-attend
(function () {
  const RELAY = "信号扱い";
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const style = document.createElement("style");
  style.textContent = `
    .ra-modal { position: fixed; inset: 0; background: rgba(0,0,0,.55); display: grid; place-items: center; padding: 16px; z-index: 50; }
    .ra-sheet { background: var(--panel, #22304a); color: var(--fg, #e8eef6); border: 1px solid var(--line, #33445c); border-radius: 12px; padding: 18px;
      width: min(380px, 100%); max-height: calc(100vh - 32px); overflow: auto; display: grid; gap: 12px; font-size: 14px; line-height: 1.5; }
    .ra-sheet h2 { margin: 0; font-size: 18px; }
    .ra-sheet .ra-msg { color: var(--muted, #93a3b8); font-size: 13px; }
    .ra-sheet .ra-err { color: #ff8a80; font-size: 13px; }
    .ra-sheet .ra-err:empty { display: none; }
    .ra-buttons { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .ra-sheet button { font: inherit; font-size: 16px; padding: 10px; border-radius: 8px; border: 1px solid var(--line, #33445c); background: var(--bar, #26354b); color: var(--fg, #e8eef6); cursor: pointer; }
    .ra-sheet button.ra-go { background: var(--info, #2f6fc0); border-color: #4b7fc4; color: #fff; font-weight: 700; }
    .ra-sheet button:disabled { opacity: .55; cursor: default; }
    .ra-stations { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 6px; }
    .ra-stations button { display: grid; gap: 1px; text-align: left; padding: 8px 10px; }
    .ra-stations button small { font-size: 12px; color: var(--muted, #93a3b8); }
    .ra-stations button small.free { color: #8fd3a8; }
    .ra-stations button.on { border-color: #e6c35c; }
    .ra-panel { display: grid; gap: 4px; }
    .ra-row { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: baseline; }
    .ra-role { flex: none; min-width: 5em; font-size: 12px; font-weight: 700; color: var(--muted, #93a3b8); }
    .ra-name { font-weight: 700; }
    .ra-none { color: var(--muted, #93a3b8); font-size: 12px; }
    .ra-free { color: #8fd3a8; font-size: 12px; }
    .ra-slot { display: inline-flex; gap: 4px; align-items: baseline; border: 1px solid var(--line, #33445c); border-radius: 6px; padding: 1px 7px; }
    .ra-slot.vacant { border-style: dashed; }
    .ra-slot .ra-st { font-size: 12px; color: var(--muted, #93a3b8); }
    button.ra-leave { font: inherit; font-size: 12px; background: none; border: none; color: #9cc3f5; cursor: pointer; padding: 0; text-decoration: underline; min-width: 0; }`;
  document.head.appendChild(style);

  async function load(token) {
    const res = await fetch("/api/tid/crew/role-attendance", { headers: token ? { "X-Crew-Token": token } : {}, cache: "no-store" });
    if (!res.ok) throw new Error("出勤者を読めませんでした");
    return res.json();
  }
  async function send(token, role, on, station) {
    const res = await fetch("/api/tid/crew/role-attend", { method: "POST", headers: { "Content-Type": "application/json", "X-Crew-Token": token }, body: JSON.stringify({ role, on, station }) });
    const j = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(j.message || "うまくいきませんでした");
    return j;
  }
  const names = (people) => people.map((p) => esc(p.name)).join("、");

  // その役職で出勤していれば、すぐ go(駅)。していなければポップを出す（出勤してから go(駅)、［やめる］で何もしない）
  async function guard(role, token, go) {
    if (!token) { go(""); return; } // ログインしていなければ、開いた画面がログインを求める
    let j = null;
    try { j = await load(token); } catch (e) { /* 読めなければポップで出勤してもらう */ }
    if (j && (j.mine || []).includes(role)) { go(role === RELAY ? j.myStation || "" : ""); return; }
    const r = j && j.roles.find((x) => x.role === role);
    const relay = role === RELAY;
    const m = document.createElement("div");
    m.className = "ra-modal";
    m.innerHTML = `<div class="ra-sheet" role="dialog" aria-modal="true">
      <h2>${esc(role)}として出勤</h2>
      <div class="ra-msg">${relay ? "扱う駅を選んで出勤してください。" : "この画面を開く前に出勤してください。"}出勤すると、出勤者の欄に名前が出ます。終わったら出勤者の欄の［退勤］を押してください。</div>
      ${relay ? `<div class="ra-stations">${r && r.stations && r.stations.length ? r.stations.map((s) => s.people.length
          ? `<button type="button" data-st="${esc(s.id)}"><span>${esc(s.name)}</span><small>${names(s.people)}</small></button>`
          : `<button type="button" data-st="${esc(s.id)}"><span>${esc(s.name)}</span><small class="free">空き</small></button>`).join("")
        : `<span class="ra-none">信号制御盤のある駅を読めませんでした</span>`}</div>` : ""}
      <div class="ra-err"></div>
      <div class="ra-buttons"><button type="button" class="ra-cancel">やめる</button><button type="button" class="ra-go"${relay ? " disabled" : ""}>出勤して開く</button></div></div>`;
    document.body.appendChild(m);
    let station = "";
    const goBtn = m.querySelector(".ra-go");
    m.querySelectorAll("button[data-st]").forEach((b) => b.onclick = () => {
      station = b.dataset.st;
      m.querySelectorAll("button[data-st]").forEach((x) => x.classList.toggle("on", x === b));
      goBtn.disabled = false;
      goBtn.textContent = `${b.querySelector("span").textContent}で出勤して開く`;
    });
    const close = () => m.remove();
    m.querySelector(".ra-cancel").onclick = close;
    m.addEventListener("click", (ev) => { if (ev.target === m) close(); });
    goBtn.onclick = async () => {
      goBtn.disabled = true;
      try { await send(token, role, true, station); close(); go(station); }
      catch (e) { m.querySelector(".ra-err").textContent = e.message; goBtn.disabled = false; }
    };
    if (!relay) goBtn.focus();
  }

  // 出勤者の欄。el の中身を書き換える。getToken() は今の合い札（なければ空）
  function panel(el, getToken) {
    const leave = (r, p) => p.me ? ` <button type="button" class="ra-leave" data-role="${esc(r)}">退勤</button>` : "";
    async function draw() {
      if (!el.isConnected) { clearInterval(timer); return; }
      let j;
      try { j = await load(getToken()); } catch (e) { return; }
      el.innerHTML = `<div class="ra-panel">${j.roles.map((r) => `<div class="ra-row"><span class="ra-role">${esc(r.role)}</span>${
        r.stations
          // 信号扱い: 駅ごとの枠（だれもいなければ空き）
          ? (r.stations.length ? r.stations.map((s) => `<span class="ra-slot${s.people.length ? "" : " vacant"}"><span class="ra-st">${esc(s.name)}</span>${
              s.people.length ? s.people.map((p) => `<span class="ra-name">${esc(p.name)}</span>${leave(r.role, p)}`).join("") : `<span class="ra-free">空き</span>`}</span>`).join("")
            : `<span class="ra-none">信号制御盤のある駅がありません</span>`)
          : r.people.length ? r.people.map((p) => `<span class="ra-name">${esc(p.name)}</span>${leave(r.role, p)}`).join("<span class='ra-none'>、</span>")
            : `<span class="ra-none">いません</span>`}</div>`).join("")}</div>`;
      el.querySelectorAll(".ra-leave").forEach((b) => b.onclick = async () => {
        if (!confirm(`${b.dataset.role}を退勤しますか？`)) return;
        try { await send(getToken(), b.dataset.role, false); } catch (e) { alert(e.message); }
        draw();
      });
    }
    const timer = setInterval(draw, 10000);
    draw();
    return draw;
  }

  window.GokkoRoleAttend = { guard, panel };
})();
