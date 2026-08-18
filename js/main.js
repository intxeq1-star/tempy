window.VeraultFilm = function initFilm() {
  const section = document.getElementById("film");
  const canvas = document.getElementById("filmCanvas");
  if (!section || !canvas || !window.gsap) return;

  const frames = [
    { src: "assets/images/hero-still.jpg", index: "01 / 07", title: "Soie", line: "Gold is not a color. It is a temperature." },
    { src: "assets/images/atelier.jpg", index: "02 / 07", title: "L’Atelier", line: "One bench. One lamp. One pair of hands." },
    { src: "assets/images/hands.jpg", index: "03 / 07", title: "Le Geste", line: "Time is worn, never announced." },
    { src: "assets/images/watch-orion.jpg", index: "04 / 07", title: "Orion", line: "Forty-one millimeters of quiet." },
    { src: "assets/images/watch-nocturne.jpg", index: "05 / 07", title: "Nocturne", line: "The night, made mechanical." },
    { src: "assets/images/parfum.jpg", index: "06 / 07", title: "Ambre", line: "A fragrance reserved for the house." },
    { src: "assets/images/salon.jpg", index: "07 / 07", title: "Le Salon", line: "By invitation. Never by accident." },
  ];

  const title = document.getElementById("filmTitle");
  const line = document.getElementById("filmLine");
  const index = document.getElementById("filmIndex");
  const bar = document.getElementById("filmBar");
  const ctx = canvas.getContext("2d", { alpha: false });
  let lastChapter = -1;

  const load = (src) =>
    new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = () => resolve(img);
      img.src = src;
    });

  Promise.all(frames.map((f) => load(f.src))).then((imgs) => {
    frames.forEach((f, i) => {
      f.img = imgs[i];
    });
    resize();
    render(0);
  });

  const resize = () => {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = canvas.clientWidth * dpr;
    canvas.height = canvas.clientHeight * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  };
  window.addEventListener("resize", resize);

  function coverDraw(img, w, h, zoom = 1, panX = 0, panY = 0) {
    if (!img || !img.width) return;
    const ir = img.width / img.height;
    const cr = w / h;
    let dw;
    let dh;
    if (ir > cr) {
      dh = h * zoom;
      dw = dh * ir;
    } else {
      dw = w * zoom;
      dh = dw / ir;
    }
    ctx.drawImage(img, (w - dw) / 2 + panX * w * 0.04, (h - dh) / 2 + panY * h * 0.03, dw, dh);
  }

  function render(progress) {
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    const max = frames.length - 1;
    const pos = progress * max;
    const i = Math.min(max, Math.floor(pos));
    const local = pos - i;
    const next = Math.min(max, i + 1);
    const fade = local > 0.72 ? (local - 0.72) / 0.28 : 0;

    ctx.fillStyle = "#000";
    ctx.fillRect(0, 0, w, h);
    coverDraw(frames[i].img, w, h, 1.04 + local * 0.16, local, local * 0.4);
    if (fade > 0 && frames[next]) {
      ctx.save();
      ctx.globalAlpha = fade;
      coverDraw(frames[next].img, w, h, 1.05 + fade * 0.06, 0, 0);
      ctx.restore();
    }

    if (i !== lastChapter && title) {
      lastChapter = i;
      const ch = frames[i];
      index.textContent = ch.index;
      title.textContent = ch.title;
      line.textContent = ch.line;
      gsap.fromTo(
        [title, line, index],
        { opacity: 0, y: 18 },
        { opacity: 1, y: 0, duration: 0.7, stagger: 0.06, ease: "power3.out" }
      );
    }
    if (bar) bar.style.width = `${progress * 100}%`;
  }

  ScrollTrigger.create({
    trigger: section,
    start: "top top",
    end: "bottom bottom",
    onUpdate: (self) => render(self.progress),
  });
};
