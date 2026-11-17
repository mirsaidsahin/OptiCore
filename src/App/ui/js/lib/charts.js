// Charts drawn by hand in SVG: no chart library, full control over the look.
import { html, useMemo, useRef, useState, useEffect, num, clamp, cx } from './core.js';

export const PALETTE = ['#7c6cff', '#22d3ee', '#34d399', '#fbbf24', '#fb7185', '#60a5fa', '#c084fc', '#f472b6', '#2dd4bf', '#fb923c', '#94a3b8', '#a3e635'];

let uid = 0;
const useId = (prefix) => useMemo(() => prefix + ++uid, []);

/** Observed width of an element, for pixel-exact SVG. */
export function useWidth(initial = 600) {
  const ref = useRef(null);
  const [width, setWidth] = useState(initial);
  useEffect(() => {
    if (!ref.current) return undefined;
    const ro = new ResizeObserver((entries) => {
      const w = Math.round(entries[0].contentRect.width);
      if (w > 0) setWidth(w);
    });
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, []);
  return [ref, width];
}

/** Area sparkline that stretches to its container. */
export function Spark({ data, height = 44, color = 'var(--accent)', max, min = 0, fill = true }) {
  const id = useId('sp');
  const n = data ? data.length : 0;
  if (n < 2) return html`<svg width="100%" height=${height}></svg>`;
  const hi = max ?? Math.max(...data, 1e-9) * 1.18;
  const W = 100, H = height;
  const y = (v) => H - 2 - ((clamp(v, min, hi) - min) / (hi - min || 1)) * (H - 5);
  let d = '';
  for (let i = 0; i < n; i++) d += (i ? 'L' : 'M') + ((i / (n - 1)) * W).toFixed(2) + ' ' + y(data[i]).toFixed(2);
  return html`<svg width="100%" height=${H} viewBox=${`0 0 ${W} ${H}`} preserveAspectRatio="none">
    <defs><linearGradient id=${id} x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" style=${{ stopColor: color, stopOpacity: 0.34 }} /><stop offset="1" style=${{ stopColor: color, stopOpacity: 0 }} />
    </linearGradient></defs>
    ${fill && html`<path d=${d + `L${W} ${H}L0 ${H}Z`} fill=${`url(#${id})`} />`}
    <path d=${d} fill="none" style=${{ stroke: color }} stroke-width="1.8" vector-effect="non-scaling-stroke" stroke-linejoin="round" stroke-linecap="round" />
  </svg>`;
}

/**
 * Multi-series line chart with a value axis and a hover read-out.
 * series: [{label, data: number[], color}], labels: optional x labels (same length as data).
 */
export function LineChart({ series, height = 190, max, min = 0, format = (v) => num(v), labels, area = true, ticks = 4 }) {
  const id = useId('lc');
  const [ref, width] = useWidth();
  const [hover, setHover] = useState(null);
  const n = Math.max(0, ...series.map((s) => s.data.length));
  const padL = 44, padR = 10, padT = 10, padB = labels ? 22 : 8;
  const W = Math.max(120, width), H = height;
  const all = series.flatMap((s) => s.data);
  const hi = max ?? (Math.max(...all, 1e-9) * 1.12 || 1);
  const x = (i) => padL + (n > 1 ? (i / (n - 1)) * (W - padL - padR) : 0);
  const y = (v) => padT + (1 - (clamp(v, min, hi) - min) / (hi - min || 1)) * (H - padT - padB);
  const path = (data) => data.map((v, i) => (i ? 'L' : 'M') + x(i).toFixed(1) + ' ' + y(v).toFixed(1)).join('');

  const onMove = (e) => {
    if (n < 2) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const i = Math.round(((e.clientX - rect.left - padL) / (W - padL - padR)) * (n - 1));
    setHover(clamp(i, 0, n - 1));
  };

  return html`<div ref=${ref} style=${{ position: 'relative' }}>
    <svg width=${W} height=${H} onMouseMove=${onMove} onMouseLeave=${() => setHover(null)}>
      <defs>${series.map((s, k) => html`<linearGradient key=${k} id=${id + '-' + k} x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" style=${{ stopColor: s.color || PALETTE[k], stopOpacity: 0.24 }} /><stop offset="1" style=${{ stopColor: s.color || PALETTE[k], stopOpacity: 0 }} />
      </linearGradient>`)}</defs>
      ${Array.from({ length: ticks + 1 }, (_, i) => {
        const v = min + ((hi - min) * i) / ticks;
        return html`<g key=${i}>
          <line x1=${padL} x2=${W - padR} y1=${y(v)} y2=${y(v)} style=${{ stroke: 'var(--line)' }} stroke-dasharray=${i === 0 ? '' : '3 4'} />
          <text class="axis" x=${padL - 8} y=${y(v) + 3.5} text-anchor="end">${format(v)}</text>
        </g>`;
      })}
      ${labels && n > 1 && [0, Math.floor((n - 1) / 2), n - 1].map((i) => html`<text key=${i} class="axis" x=${x(i)} y=${H - 5} text-anchor=${i === 0 ? 'start' : i === n - 1 ? 'end' : 'middle'}>${labels[i]}</text>`)}
      ${series.map((s, k) => s.data.length > 1 && html`<g key=${k}>
        ${area && html`<path d=${path(s.data) + `L${x(s.data.length - 1)} ${H - padB}L${padL} ${H - padB}Z`} fill=${`url(#${id}-${k})`} />`}
        <path d=${path(s.data)} fill="none" style=${{ stroke: s.color || PALETTE[k] }} stroke-width="1.9" stroke-linejoin="round" stroke-linecap="round" />
      </g>`)}
      ${hover != null && html`<g>
        <line x1=${x(hover)} x2=${x(hover)} y1=${padT} y2=${H - padB} style=${{ stroke: 'var(--line-2)' }} />
        ${series.map((s, k) => s.data[hover] != null && html`<circle key=${k} cx=${x(hover)} cy=${y(s.data[hover])} r="3.5" style=${{ fill: s.color || PALETTE[k], stroke: 'var(--card)' }} stroke-width="2" />`)}
      </g>`}
    </svg>
    ${hover != null && html`<div class="card pad small" style=${{ position: 'absolute', top: '4px', left: Math.min(W - 170, Math.max(padL, x(hover) + 10)) + 'px', padding: '6px 9px', pointerEvents: 'none', boxShadow: 'var(--shadow)' }}>
      ${labels && html`<div class="dim tiny">${labels[hover]}</div>`}
      ${series.map((s, k) => html`<div key=${k} class="row gap-s num"><i style=${{ width: '8px', height: '8px', borderRadius: '2px', background: s.color || PALETTE[k] }}></i>${s.label}: <b>${s.data[hover] != null ? format(s.data[hover]) : '–'}</b></div>`)}
    </div>`}
  </div>`;
}

/** Circular gauge, 0..100. Children are centered inside. */
export function Ring({ value, size = 150, stroke = 12, color, children }) {
  const id = useId('rg');
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const v = clamp(value || 0, 0, 100);
  return html`<div class="ring-wrap" style=${{ width: size + 'px', height: size + 'px' }}>
    <svg width=${size} height=${size} style=${{ transform: 'rotate(-90deg)' }}>
      <defs><linearGradient id=${id} x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" style=${{ stopColor: color || 'var(--accent)' }} /><stop offset="1" style=${{ stopColor: color || 'var(--accent-2)' }} />
      </linearGradient></defs>
      <circle cx=${size / 2} cy=${size / 2} r=${r} fill="none" style=${{ stroke: 'var(--line)' }} stroke-width=${stroke} />
      <circle cx=${size / 2} cy=${size / 2} r=${r} fill="none" stroke=${`url(#${id})`} stroke-width=${stroke} stroke-linecap="round"
        stroke-dasharray=${c} stroke-dashoffset=${c * (1 - v / 100)} style=${{ transition: 'stroke-dashoffset .9s cubic-bezier(.2,.8,.2,1)' }} />
    </svg>
    <div class="ring-center">${children}</div>
  </div>`;
}

/** Donut of segments: [{label, value, color}]. */
export function Donut({ segments, size = 140, stroke = 18, children }) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const total = segments.reduce((s, x) => s + x.value, 0) || 1;
  let offset = 0;
  return html`<div class="ring-wrap" style=${{ width: size + 'px', height: size + 'px' }}>
    <svg width=${size} height=${size} style=${{ transform: 'rotate(-90deg)' }}>
      <circle cx=${size / 2} cy=${size / 2} r=${r} fill="none" style=${{ stroke: 'var(--line)' }} stroke-width=${stroke} />
      ${segments.map((s, i) => {
        const len = (s.value / total) * c;
        const el = html`<circle key=${i} cx=${size / 2} cy=${size / 2} r=${r} fill="none" style=${{ stroke: s.color || PALETTE[i % PALETTE.length] }}
          stroke-width=${stroke} stroke-dasharray=${`${Math.max(0, len - 1.5)} ${c}`} stroke-dashoffset=${-offset} />`;
        offset += len;
        return el;
      })}
    </svg>
    <div class="ring-center">${children}</div>
  </div>`;
}

/** Horizontal ranked bars: [{label, value, right, color, sub, icon}]. */
export function BarList({ items, max, format = (v) => num(v), onPick }) {
  const hi = max ?? Math.max(...items.map((i) => i.value), 1e-9);
  return html`<div class="col" style=${{ gap: '9px' }}>
    ${items.map((it, i) => html`<div key=${it.key ?? it.label ?? i} class=${cx(onPick && 'click')} style=${onPick ? { cursor: 'pointer' } : null} onClick=${onPick ? () => onPick(it) : null}>
      <div class="row small" style=${{ marginBottom: '4px' }}>
        ${it.icon}
        <span class="grow ell">${it.label}${it.sub && html`<span class="dim"> · ${it.sub}</span>`}</span>
        <span class="num b">${it.right ?? format(it.value)}</span>
      </div>
      <div class="bar"><i style=${{ width: clamp((it.value / hi) * 100, 0, 100) + '%', background: it.color || 'var(--accent)' }}></i></div>
    </div>`)}
  </div>`;
}

// ---------------------------------------------------------------- treemap
function squarify(items, rect) {
  const total = items.reduce((s, i) => s + i.size, 0);
  const out = [];
  if (total <= 0 || rect.w <= 0 || rect.h <= 0) return out;
  const scale = (rect.w * rect.h) / total;
  let nodes = items.map((item) => ({ item, area: item.size * scale })).filter((n) => n.area > 0);
  let { x, y, w, h } = rect;
  const worst = (row, sum, side) => {
    let mx = 0, mn = Infinity;
    for (const n of row) { if (n.area > mx) mx = n.area; if (n.area < mn) mn = n.area; }
    return Math.max((side * side * mx) / (sum * sum), (sum * sum) / (side * side * mn));
  };
  while (nodes.length) {
    const side = Math.min(w, h);
    let count = 1, sum = nodes[0].area;
    while (count < nodes.length) {
      const nextSum = sum + nodes[count].area;
      if (worst(nodes.slice(0, count + 1), nextSum, side) > worst(nodes.slice(0, count), sum, side)) break;
      sum = nextSum;
      count++;
    }
    const thick = sum / side;
    let off = 0;
    for (const n of nodes.slice(0, count)) {
      const len = n.area / thick;
      out.push(w >= h ? { item: n.item, x, y: y + off, w: thick, h: len } : { item: n.item, x: x + off, y, w: len, h: thick });
      off += len;
    }
    if (w >= h) { x += thick; w -= thick; } else { y += thick; h -= thick; }
    nodes = nodes.slice(count);
  }
  return out;
}

/** items: [{name, size, ...}] sorted or not; onPick(item) on click. */
export function Treemap({ items, height = 360, onPick, label = (i) => i.name, sub, color }) {
  const [ref, width] = useWidth(800);
  const rects = useMemo(
    () => squarify([...items].filter((i) => i.size > 0).sort((a, b) => b.size - a.size).slice(0, 80), { x: 0, y: 0, w: width, h: height }),
    [items, width, height]);
  return html`<div ref=${ref} class="tm" style=${{ height: height + 'px' }}>
    ${rects.map((r, i) => html`<div key=${r.item.key ?? r.item.name ?? i} class="tm-cell" title=${label(r.item) + (sub ? ' · ' + sub(r.item) : '')}
      style=${{ left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', background: color ? color(r.item, i) : PALETTE[i % PALETTE.length] }}
      onClick=${onPick ? () => onPick(r.item) : null}>
      ${r.w > 54 && r.h > 22 && html`<b>${label(r.item)}</b>`}
      ${sub && r.w > 54 && r.h > 40 && html`<span>${sub(r.item)}</span>`}
    </div>`)}
  </div>`;
}

export const Legend = ({ items }) => html`<div class="legend">
  ${items.map((it, i) => html`<span key=${i}><i style=${{ background: it.color || PALETTE[i % PALETTE.length] }}></i>${it.label}${it.value != null && html` <b class="num">${it.value}</b>`}</span>`)}
</div>`;
