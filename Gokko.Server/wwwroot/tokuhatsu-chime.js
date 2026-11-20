// 特発が作動したときのチャイム（ﾍﾟﾛﾍﾟﾛﾍﾟﾛﾍﾟﾛﾍﾟﾛ の電子音）。Web Audio で作る（音のファイルは使わない）。
// 使い方: GokkoChime.mount(要素) で「作動チャイム」の入り切りと試聴のボタンを置き、
// 特発の一覧を受け取るたびに GokkoChime.watch(作動中の踏切名の配列) を呼ぶ（新しく作動した踏切があれば鳴らす。最初の1回は鳴らさない）。
// ブラウザの決まりで、画面を一度クリック・キー操作するまでは音が出ない。
window.GokkoChime = (() => {
  const STORE = "gokkoTokuhatsuChime";
  let enabled = true;
  try { enabled = localStorage.getItem(STORE) !== "off"; } catch (e) {}
  let ctx = null;
  let known = null; // 前回の作動中の踏切（null なら、まだ1回も受け取っていない）
  let note = null;

  function ensure() {
    if (!ctx) {
      try { ctx = new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { return null; }
    }
    if (ctx.state === "suspended") ctx.resume().catch(() => {});
    return ctx;
  }
  // 最初のクリック・キー操作で音を出せる状態にする
  const unlock = () => { ensure(); updateNote(); };
  window.addEventListener("pointerdown", unlock, true);
  window.addEventListener("keydown", unlock, true);

  // ﾍﾟﾛ を5回。ﾍﾟ（高い音）と ﾛ（低い音）を短く交互に、矩形波を少し丸めて鳴らす
  function play() {
    const c = ensure();
    if (!c) return;
    const t0 = c.currentTime + 0.02;
    const out = c.createGain(); out.gain.value = 0.18;
    const lp = c.createBiquadFilter(); lp.type = "lowpass"; lp.frequency.value = 3200;
    lp.connect(out); out.connect(c.destination);
    const step = 0.085;
    for (let n = 0; n < 10; n++) {
      const t = t0 + n * step;
      const hi = n % 2 === 0;
      const osc = c.createOscillator(); osc.type = "square";
      osc.frequency.setValueAtTime(hi ? 1560 : 1170, t);
      osc.frequency.linearRampToValueAtTime(hi ? 1480 : 1240, t + step); // 少し滑らせて「ﾍﾟﾛ」らしく
      const g = c.createGain();
      g.gain.setValueAtTime(0, t);
      g.gain.linearRampToValueAtTime(1, t + 0.006);
      g.gain.setValueAtTime(1, t + step - 0.012);
      g.gain.linearRampToValueAtTime(0, t + step);
      osc.connect(g); g.connect(lp);
      osc.start(t); osc.stop(t + step + 0.01);
    }
  }

  function watch(names) {
    const now = new Set(names);
    const fresh = known ? names.filter((n) => !known.has(n)) : [];
    known = now;
    if (fresh.length > 0 && enabled) play();
    return fresh;
  }

  function updateNote() {
    if (!note) return;
    note.textContent = enabled && (!ctx || ctx.state !== "running") ? "（画面をクリックすると鳴るようになります）" : "";
  }

  function mount(el) {
    el.innerHTML = `<label title="特発が作動したら、この画面でチャイムを鳴らす"><input type="checkbox"> 作動チャイム</label>
      <button type="button">試聴</button> <span class="muted" style="font-size:12px"></span>`;
    const box = el.querySelector("input");
    box.checked = enabled;
    note = el.querySelector("span");
    box.onchange = () => {
      enabled = box.checked;
      try { localStorage.setItem(STORE, enabled ? "on" : "off"); } catch (e) {}
      updateNote();
    };
    el.querySelector("button").onclick = () => play();
    updateNote();
    setInterval(updateNote, 1000);
  }

  return { mount, watch, play };
})();
