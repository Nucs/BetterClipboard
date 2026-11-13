/**
 * Puts a Store listing's desktop screenshots into a wanted order, on Partner Center's listing page of one language.
 *
 * Why this exists: importing a listings file does not keep the order of a listing's screenshots (measured
 * 2026-11-13: after each of three imports some languages' pictures were in another order, also languages the
 * file had not changed, and a file that asked for the right order did not restore it). Microsoft's supported way
 * to order them is the listing page itself: "you can drag and drop them to reorder them". On that page a screenshot
 * dragged onto another one changes places with it, and the page saves the new order at once, without its Save
 * button. A caption stays with its picture.
 *
 * This function does that dragging with the page's own drag events, so it needs no mouse and no visible tab:
 * pass it to the browser tools' evaluate_script on the listing page of ONE language (or paste it into that page's
 * console and call it), with that language's captions in the wanted order. docs/store-listing.md has them: the
 * "Screenshot captions" block of the language, first line first. After it ran for every language that
 * `make-store-listing-csv.py --check` named, export the listings again and run that check once more.
 *
 * How pictures are told apart: by the caption each card shows when the page has just loaded. After a swap the page
 * shows "Add image caption" on the cards it moved until it is loaded again (the captions are still saved with
 * their pictures), so the cards are followed by their picture's address from then on. Run it on a freshly loaded
 * page for that reason; a second run without a reload stops and says so.
 *
 * What it touches: only the order of the desktop screenshots of the language whose page is open. It presses no
 * button, uploads and deletes nothing, and leaves every other field alone.
 *
 * @param {string[]} wantedCaptions The captions of the listing's screenshots, in the order the screenshots must
 *     have. Every screenshot on the page must carry exactly one of them, and each of them must be on the page.
 * @returns {Promise<{ok: boolean, swaps: string[], order: number[], error?: string}>} `ok` when the page shows
 *     the wanted order at the end; `swaps` lists the exchanges made ("5<->1" exchanges the fifth and the first
 *     screenshot); `order` is the final order as numbers of the wanted list (1, 2, 3, ... when all is right);
 *     `error` says why nothing more was done (the page is not a listing page, a card's picture did not load, a
 *     caption is unknown, a swap did not show).
 */
async function orderStoreScreenshots(wantedCaptions) {
  // Waits `milliseconds` without blocking the page (a timer calls the promise's `resolve`): the page needs its own
  // turns to draw and to save.
  const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
  // The screenshot cards as the page shows them now, first one first. Each sits in a wrapper that can be dragged;
  // asked anew every time, because the page draws new cards after a swap.
  const cards = () => [...document.querySelectorAll('.screenshot-swap-container')];
  // A card's picture, as the address of its image (the page gives every picture its own); '' while it loads.
  const pictureOf = card => { const image = card.querySelector('img'); return image ? (image.currentSrc || image.src || '') : ''; };
  // A card's caption as shown under its picture. Only right on a freshly loaded page (see above).
  const captionOf = card => ((card.querySelector('.asset-card .spacer-sm-top') || card).innerText || '').trim();

  if (!/\/submissions\/\d+\/listings/.test(location.pathname)) {
    return { ok: false, swaps: [], order: [], error: 'this is not a Store listing page of a submission' };
  }
  // The cards and their pictures arrive after the page: wait until every card has its picture.
  const loaded = Date.now() + 45000;
  while (Date.now() < loaded && (cards().length < wantedCaptions.length || cards().some(card => !pictureOf(card)))) {
    await pause(500);
  }
  if (cards().length !== wantedCaptions.length || cards().some(card => !pictureOf(card))) {
    return { ok: false, swaps: [], order: [], error: `the page shows ${cards().length} screenshots with pictures, not ${wantedCaptions.length}` };
  }

  // Which picture carries which caption, read once while the page still shows every caption.
  const wantedIndex = new Map();   // picture address -> its place in the wanted order
  for (const card of cards()) {
    const index = wantedCaptions.indexOf(captionOf(card));
    if (index < 0 || [...wantedIndex.values()].includes(index)) {
      return { ok: false, swaps: [], order: [], error: `a screenshot's caption is not one of the wanted captions, or is there twice ("${captionOf(card).slice(0, 40)}"); load the page again before a second run` };
    }
    wantedIndex.set(pictureOf(card), index);
  }
  // The page's order now, as places in the wanted order; -1 for a card whose picture is not back yet.
  const order = () => cards().map(card => wantedIndex.has(pictureOf(card)) ? wantedIndex.get(pictureOf(card)) : -1);
  // The order once the page has finished drawing: after a swap it draws the cards again, and a card without its
  // picture cannot be told from the others. Gives up after 20 seconds and returns what it sees (with its -1).
  const settled = async () => {
    const until = Date.now() + 20000;
    while (Date.now() < until && order().includes(-1)) await pause(500);
    return order();
  };

  const swaps = [];
  for (let position = 0; position < wantedCaptions.length; position++) {
    let now = await settled();
    if (now.includes(-1)) return { ok: false, swaps, order: now.map(index => index + 1), error: 'a picture did not come back after a swap' };
    if (now[position] === position) continue;
    const from = now.indexOf(position);
    const source = cards()[from], target = cards()[position];
    // The page's own drag-and-drop: the dragged screenshot and the one it is dropped on change places.
    const transfer = new DataTransfer();
    // Sends one drag event of the given `type` to `element` (a card's wrapper), with the same data object from start
    // to end, as a real drag has.
    const fire = (element, type) => element.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, composed: true, dataTransfer: transfer }));
    fire(source, 'dragstart');
    await pause(150);
    fire(target, 'dragenter');
    fire(target, 'dragover');
    await pause(150);
    fire(target, 'drop');
    fire(source, 'dragend');
    await pause(4000);   // the page saves the new order by itself; give it time before the next swap
    now = await settled();
    swaps.push(`${from + 1}<->${position + 1}`);
    if (now[position] !== position) return { ok: false, swaps, order: now.map(index => index + 1), error: 'a swap did not show on the page' };
  }
  const final = await settled();
  return { ok: final.every((index, position) => index === position), swaps, order: final.map(index => index + 1) };
}
