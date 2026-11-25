// 特発臨（運転整理のために、司令員がその場で立てる臨時列車）の欄。司令用管理画面（diasettings.html）で使う。
// 駅の番線にいる列車を選び、その駅・番線から始まるスジ（列番・種別・行先・停車駅・時刻）を立てる。
// 時刻は標準運転時分から引いた案を、表で直してから立てる。元の列車はその駅で打ち切りになる。
// 自動運転の列車は、その駅で特発臨の列番に変わって走る。ユーザーの列車は、タブレットに特発臨が出る（列番を入れ直してもらう）。
// ほかの欄から列車を選んで開くときは GokkoRinji.pick(列番)。
window.GokkoRinji = (() => {
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const suffix = { 特急: "A", 急行: "B", 準急: "C", 区急: "D", 快急: "K" };
  let picker = null;

  // 列番の案: 元の列車の数字を使い、向きに合わせて最後の桁を奇数（下り）・偶数（上り）にする。回送は「回」、ほかは「臨」を頭に付ける
  function suggest(from, type, down) {
    const m = String(from || "").match(/(\d+)(?!.*\d)/);
    if (!m) return "";
    let d = m[1];
    const last = Number(d[d.length - 1]);
    if ((last % 2 === 1) !== down) d = d.slice(0, -1) + (last === 9 ? 8 : last + 1);
    return (type === "回送" ? "回" : "臨") + d + (suffix[type] || "");
  }

  function panel({ el, api, act, onMessage }) {
    let data = { candidates: [], list: [], types: [], stations: [] };
    const form = { train: "", type: "普通", number: "", numberTouched: false, dest: "", dep: "", linkTurn: true };
    let plan = null; // スジの案（表で直す）
    const msg = (m) => onMessage && onMessage(m);

    el.innerHTML = `<h2>特発臨（運転整理の臨時列車）</h2>
      <div data-list></div>
      <div class="row" style="margin-top:8px">
        <select data-f="train"></select>
        <select data-f="type"></select>
        <select data-f="dest"></select>
        <label>列番 <input data-f="number" size="10" placeholder="臨1347"></label>
        <label>発車 <input data-f="dep" size="8" placeholder="3分後"></label>
        <button data-draft class="primary">時刻を引く</button>
      </div>
      <div data-plan></div>
      <div class="muted" style="margin-top:6px">駅の番線に止まっている列車から、その駅・番線を発駅にして立てます。時刻は標準運転時分から引いた案です（表で直せます）。途中駅・終点の番線は空けておくと、自動進路構成が決めます。
        立てると、元の列車はその駅で打ち切りになり、元の列車の後運用は時刻表どおりに残ります（「元の列車の後運用につなぐ」を選ぶと、特発臨の終点からつなぎます）。
        自動運転の列車は、その駅に止まったら特発臨の列番に変わって走ります。ユーザーの列車は、タブレットに特発臨が出るので、運転士に列番を入れ直してもらってください。
        ロープレ時刻を設定するか、サーバーを再起動すると消えます。</div>`;
    const $ = (sel) => el.querySelector(sel);
    const f = (k) => el.querySelector(`[data-f="${k}"]`);

    const cand = () => data.candidates.find((c) => c.train === form.train);
    const zoneOf = (id) => (data.stations.find((s) => s.id === id) || {}).zone;

    function fillSelects() {
      const c = cand();
      const setOpts = (sel, html) => { if (document.activeElement !== sel && sel.dataset.html !== html) { sel.innerHTML = html; sel.dataset.html = html; } };
      const trainOpts = [`<option value="">元の列車を選ぶ（駅の番線に止まっている列車）</option>`].concat(data.candidates.map((x) =>
        `<option value="${esc(x.train)}" ${x.train === form.train ? "selected" : ""}>${esc(x.train)}（${x.ai ? "自動運転" : "ユーザー"}・${esc(x.stationName)} ${x.track}番線・停車中${x.rinji ? `・特発臨 ${esc(x.rinji)} 待ち` : ""}）</option>`));
      if (form.train && !c) trainOpts.push(`<option value="${esc(form.train)}" selected>${esc(form.train)}（いまは駅の番線に止まっていません）</option>`);
      setOpts(f("train"), trainOpts.join(""));
      setOpts(f("type"), data.types.map((t) => `<option ${t === form.type ? "selected" : ""}>${esc(t)}</option>`).join(""));
      const hz = c ? zoneOf(c.station) : null;
      setOpts(f("dest"), [`<option value="">行先</option>`].concat(data.stations.filter((s) => !c || s.id !== c.station).map((s) =>
        `<option value="${esc(s.id)}" ${s.id === form.dest ? "selected" : ""}>${esc(s.name)}${hz == null ? "" : s.zone > hz ? "（下り）" : "（上り）"}</option>`)).join(""));
    }

    function autoNumber() {
      const c = cand();
      if (form.numberTouched || !c || !form.dest) return;
      form.number = suggest(c.train, form.type, zoneOf(form.dest) > zoneOf(c.station));
      f("number").value = form.number;
    }

    function drawList() {
      const html = data.list.length === 0 ? `<div class="muted">立てた特発臨はありません</div>`
        : `<table><tr class="muted"><td>列番</td><td>種別</td><td>元の列車</td><td>発</td><td>行先</td><td>後運用</td><td>状態</td><td></td></tr>${data.list.map((r) => `
          <tr><td><b>${esc(r.number)}</b></td><td>${esc(r.type)}</td><td>${esc(r.from)}</td>
            <td>${esc(r.station)} ${r.track}番線 ${esc(r.dep)}</td><td>${esc(r.dest)} ${esc(r.arr)}着</td>
            <td>${r.turn ? esc(r.turn) : `<span class="muted">なし</span>`}</td>
            <td>${r.started ? `<span class="ok">列番を変えて走り出しました</span>` : `<span class="muted">${esc(r.from)} が ${esc(r.station)} で変わるのを待っています</span>`}</td>
            <td><button data-cancel="${esc(r.number)}" class="danger">取消</button></td></tr>`).join("")}</table>`;
      const box = $("[data-list]");
      if (box.dataset.html !== html) { box.innerHTML = html; box.dataset.html = html; }
    }

    function drawPlan() {
      if (!plan) { $("[data-plan]").innerHTML = ""; return; }
      const n = plan.stops.length;
      $("[data-plan]").innerHTML = `<div style="margin:6px 0">${esc(plan.train)} → <b>${esc(form.number || "（列番）")}</b>（${esc(plan.type)}・${esc(plan.dir)}）
          ${esc(plan.stationName)} ${plan.track}番線発</div>
        <div style="overflow-x:auto"><table>
        <tr class="muted"><td>駅</td><td>停車</td><td>着</td><td>発・通過</td><td>番線</td></tr>
        ${plan.stops.map((s, i) => {
          const fixed = i === 0 || i === n - 1;
          return `<tr data-i="${i}"><td>${esc(s.name)}</td>
            <td><label><input type="checkbox" data-stop ${s.stop === "停" ? "checked" : ""} ${fixed ? "disabled" : ""}> ${s.stop === "停" ? "停" : "通"}</label></td>
            <td>${i === 0 || s.stop !== "停" ? "" : `<input data-arr size="8" value="${esc(s.arr || "")}">`}</td>
            <td>${i === n - 1 ? "" : `<input data-dep size="8" value="${esc(s.dep || "")}">`}</td>
            <td>${i === 0 ? `${s.track}番線` : s.control && s.stop === "停" ? `<input data-track size="3" value="${s.track ?? ""}" placeholder="自動">` : ""}</td></tr>`;
        }).join("")}</table></div>
        <div class="row" style="margin-top:6px">
          <label><input type="checkbox" data-link ${form.linkTurn ? "checked" : ""}> 元の列車の後運用につなぐ（終点が同じ駅のとき）</label>
          <button data-create class="primary">特発臨を立てる</button>
          <button data-clear>やめる</button></div>`;
    }

    // 表で直した時刻・番線を、案に書き戻す
    function readPlan() {
      if (!plan) return;
      for (const tr of $("[data-plan]").querySelectorAll("tr[data-i]")) {
        const s = plan.stops[Number(tr.dataset.i)];
        const a = tr.querySelector("[data-arr]"), d = tr.querySelector("[data-dep]"), t = tr.querySelector("[data-track]");
        if (a) s.arr = a.value.trim() || null;
        if (d) s.dep = d.value.trim() || null;
        if (t) s.track = t.value.trim() === "" ? null : Number(t.value);
      }
    }

    async function draft(stopAt) {
      try {
        plan = await api("POST", "/rinji/draft", { train: form.train, type: form.type, dest: form.dest, dep: form.dep || null, stopAt: stopAt || null });
        msg("");
      } catch (e) { plan = null; msg(e.message); }
      drawPlan();
    }

    el.addEventListener("change", (ev) => {
      const t = ev.target;
      if (t.dataset.f === "train") { form.train = t.value; form.dest = ""; plan = null; drawPlan(); fillSelects(); }
      else if (t.dataset.f === "type") { form.type = t.value; autoNumber(); if (plan) { readPlan(); draft(); } }
      else if (t.dataset.f === "dest") { form.dest = t.value; autoNumber(); plan = null; drawPlan(); }
      else if (t.dataset.f === "number") { form.number = t.value.trim(); form.numberTouched = form.number !== ""; drawPlan(); }
      else if (t.dataset.f === "dep") form.dep = t.value.trim();
      else if (t.dataset.link !== undefined) form.linkTurn = t.checked;
      else if (t.dataset.stop !== undefined && plan) {
        // 停車駅を変えたら、時刻を引き直す（発車時刻は今の表のまま）
        readPlan();
        const i = Number(t.closest("tr").dataset.i);
        plan.stops[i].stop = t.checked ? "停" : "通";
        form.dep = plan.stops[0].dep || form.dep;
        f("dep").value = form.dep;
        draft(plan.stops.filter((s) => s.stop === "停").map((s) => s.st));
      }
    });

    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button");
      if (!b) return;
      if (b.dataset.draft !== undefined) {
        form.number = f("number").value.trim(); form.dep = f("dep").value.trim();
        if (!form.train) { msg("元の列車を選んでください"); return; }
        if (!form.dest) { msg("行先を選んでください"); return; }
        await draft();
      } else if (b.dataset.clear !== undefined) {
        plan = null; drawPlan();
      } else if (b.dataset.create !== undefined && plan) {
        readPlan();
        form.number = f("number").value.trim();
        if (!form.number) { msg("列番を入れてください"); return; }
        const c = cand();
        if (!(await GokkoDia.ask(`特発臨 ${form.number} を立てますか？`,
          `${plan.train} は ${plan.stationName} で打ち切りになり、${form.number}（${plan.type}）として ${plan.stops[0].dep} に ${plan.stops[plan.stops.length - 1].name} へ向けて出ます。`
          + (c && !c.ai ? "ユーザーの列車なので、運転士に列番の入れ直しを伝えてください。" : "")))) return;
        try {
          await api("POST", "/rinji", {
            train: plan.train, number: form.number, type: plan.type, linkTurn: form.linkTurn,
            stops: plan.stops.map((s) => ({ st: s.st, stop: s.stop, arr: s.arr, dep: s.dep, track: s.track }))
          });
          plan = null; form.numberTouched = false; form.number = ""; f("number").value = ""; form.dep = ""; f("dep").value = "";
          drawPlan(); msg(""); await refresh();
        } catch (e) { msg(e.message); }
      } else if (b.dataset.cancel) {
        const r = data.list.find((x) => x.number === b.dataset.cancel);
        if (!(await GokkoDia.ask(`特発臨 ${b.dataset.cancel} を取り消しますか？`,
          r && r.started ? "もう走り出しています。自動運転の列車なら消えます。" : `${r ? r.from : ""} の打ち切りも外し、元の時刻表に戻します。`))) return;
        await act("POST", "/rinji/cancel", { number: b.dataset.cancel });
        await refresh();
      }
    });

    async function refresh() {
      try { data = await api("GET", "/rinji"); } catch (e) { return; }
      fillSelects();
      drawList();
    }

    picker = (train) => {
      form.train = String(train || "").toUpperCase(); form.dest = ""; plan = null; drawPlan(); fillSelects();
      el.scrollIntoView({ behavior: "smooth" });
    };

    refresh();
    setInterval(refresh, 2000);
    return { refresh };
  }

  return { panel, suggest, pick: (train) => picker && picker(train) };
})();
