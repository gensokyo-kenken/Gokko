// 乗務メニュー（menu.html）へ戻る導線の共通部分。
// 各画面は、戻るための要素に data-menu-link を付けておく（<a href="menu.html" data-menu-link>メニュー</a> など）。
// 小窓（iframe）や ?embed=1 で埋め込まれているときは、メニューへ戻る意味がないので隠す。
(() => {
  let embedded = false;
  try { embedded = window.top !== window.self; } catch (e) { embedded = true; }
  if (new URLSearchParams(location.search).get("embed") === "1") embedded = true;
  if (!embedded) return;
  const style = document.createElement("style");
  style.textContent = "[data-menu-link] { display: none !important; }";
  document.head.appendChild(style);
})();
