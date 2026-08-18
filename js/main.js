(() => {
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const touch = window.matchMedia("(hover: none), (pointer: coarse)").matches;

  const frames = [
    {
      src: "assets/images/hero-still.jpg",
      index: "01 / 07",
      title: "Soie",
      line: "Gold is not a color. It is a temperature.",
    },
    {
      src: "assets/images/atelier.jpg",
      index: "02 / 07",
      title: "L’Atelier",
      line: "One bench. One lamp. One pair of hands.",
    },
    {
      src: "assets/images/hands.jpg",
      index: "03 / 07",
      title: "Le Geste",
      line: "Time is worn, never announced.",
    },
    {
      src: "assets/images/watch-orion.jpg",
      index: "04 / 07",
      title: "Orion",
      line: "Forty-one millimeters of quiet.",
    },
    {
      src: "assets/images/watch-nocturne.jpg",
      index: "05 / 07",
      title: "Nocturne",
      line: "The night, made mechanical.",
    },
    {
      src: "assets/images/parfum.jpg",
      index: "06 / 07",
      title: "Ambre",
      line: "A fragrance reserved for the house.",
    },
    {
      src: "assets/images/salon.jpg",
      index: "07 / 07",
      title: "Le Salon",
      line: "By invitation. Never by accident.",
    },
  ];

  const extras = [
    "assets/images/watch-orion.jpg",
    "assets/images/watch-nocturne.jpg",
    "assets/images/parfum.jpg",
    "assets/images/bijou.jpg",
    "assets/images/portrait.jpg",
    "assets/images/hero-still.jpg",
    "assets/images/grain.jpg",
  ];

  const loadImage = (src) =>
    new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = () => resolve(img);
      img.src = src;
    });

  const pad = (n) => String(Math.max(0, Math.min(100, Math.round(n)))).padStart(3, "0");

  async function boot() {
    const loader = document.getElementById("loader");
    const countEl = document.getElementById("loaderCount");
    const bar = document.querySelector(".loader-line span");
    const allSrc = [...new Set([...frames.map((f) => f.src), ...extras])];
    let done = 0;

    const loaded = await Promise.all(
      allSrc.map(async (src) => {
        const img = await loadImage(src);
        done += 1;
        const p = (done / allSrc.length) * 100;
        countEl.textContent = pad(p);
        bar.style.width = `${p}%`;
        return [src, img];
      })
    );

    const cache = Object.fromEntries(loaded);
    frames.forEach((f) => {
      f.img = cache[f.src];
    });

    await new Promise((r) => setTimeout(r, 280));
    loader.style.transition = "opacity 0.9s cubic-bezier(0.16,1,0.3,1), visibility 0.9s";
    loader.style.opacity = "0";
    loader.style.visibility = "hidden";
    document.body.classList.add("is-ready");

    initMotion(cache);
  }

  function initMotion(cache) {
    gsap.registerPlugin(ScrollTrigger);

    let lenis = null;
    if (!reduce && window.Lenis) {
      lenis = new Lenis({
        duration: 1.15,
        smoothWheel: true,
        wheelMultiplier: 0.9,
      });
      lenis.on("scroll", ScrollTrigger.update);
      gsap.ticker.add((time) => lenis.raf(time * 1000));
      gsap.ticker.lagSmoothing(0);
    }

    gsap.to(".hero-title .char", {
      y: 0,
      duration: 1.25,
      stagger: 0.055,
      ease: "power4.out",
      delay: 0.15,
    });

    gsap.to(".fade-up", {
      opacity: 1,
      y: 0,
      duration: 1.1,
      stagger: 0.12,
      ease: "power3.out",
      delay: 0.45,
    });

    gsap.utils.toArray(".reveal").forEach((el) => {
      gsap.to(el, {
        opacity: 1,
        y: 0,
        duration: 1.15,
        ease: "power3.out",
        scrollTrigger: { trigger: el, start: "top 86%" },
      });
    });

    gsap.utils.toArray(".reveal-img img").forEach((el) => {
      gsap.to(el, {
        scale: 1,
        filter: "saturate(1)",
        duration: 1.6,
        ease: "power3.out",
        scrollTrigger: { trigger: el, start: "top 88%" },
      });
    });

    const progress = document.querySelector(".progress i");
    ScrollTrigger.create({
      trigger: document.body,
      start: "top top",
      end: "bottom bottom",
      onUpdate: (self) => {
        progress.style.width = `${self.progress * 100}%`;
      },
    });

    initFilm();
    initCursor();
    initMagnetic();
    initMenu(lenis);
    initClock();
    initForm();
    initHeroParallax();
  }

  function coverDraw(ctx, img, w, h, zoom = 1, panX = 0, panY = 0) {
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
    const x = (w - dw) / 2 + panX * w * 0.04;
    const y = (h - dh) / 2 + panY * h * 0.03;
    ctx.drawImage(img, x, y, dw, dh);
  }

  function initFilm() {
    const section = document.getElementById("film");
    const canvas = document.getElementById("filmCanvas");
    const ctx = canvas.getContext("2d", { alpha: false });
    const title = document.getElementById("filmTitle");
    const line = document.getElementById("filmLine");
    const index = document.getElementById("filmIndex");
    const bar = document.getElementById("filmBar");
    let lastChapter = -1;

    const resize = () => {
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = canvas.clientWidth * dpr;
      canvas.height = canvas.clientHeight * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };
    resize();
    window.addEventListener("resize", resize);

    const render = (progress) => {
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

      const zoomA = 1.04 + local * 0.16;
      coverDraw(ctx, frames[i].img, w, h, zoomA, local, local * 0.4);

      if (fade > 0 && frames[next]) {
        ctx.save();
        ctx.globalAlpha = fade;
        coverDraw(ctx, frames[next].img, w, h, 1.05 + fade * 0.06, 0, 0);
        ctx.restore();
      }

      if (i !== lastChapter) {
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
      bar.style.width = `${progress * 100}%`;
    };

    render(0);

    ScrollTrigger.create({
      trigger: section,
      start: "top top",
      end: "bottom bottom",
      onUpdate: (self) => render(self.progress),
    });
  }

  function initCursor() {
    if (touch) return;
    const root = document.querySelector(".cursor");
    const dot = document.querySelector(".cursor-dot");
    const ring = document.querySelector(".cursor-ring");
    const label = document.querySelector(".cursor-label");
    const pos = { x: window.innerWidth / 2, y: window.innerHeight / 2 };
    const ringPos = { ...pos };
    const labels = { view: "View", enter: "Enter", scroll: "Scroll", menu: "Menu" };

    window.addEventListener("mousemove", (e) => {
      pos.x = e.clientX;
      pos.y = e.clientY;
      dot.style.transform = `translate(${pos.x}px, ${pos.y}px) translate(-50%,-50%)`;
    });

    const tick = () => {
      ringPos.x += (pos.x - ringPos.x) * 0.16;
      ringPos.y += (pos.y - ringPos.y) * 0.16;
      ring.style.transform = `translate(${ringPos.x}px, ${ringPos.y}px) translate(-50%,-50%)`;
      label.style.transform = `translate(${ringPos.x}px, ${ringPos.y}px) translate(-50%,-50%)`;
      requestAnimationFrame(tick);
    };
    tick();

    document.querySelectorAll("a, button, [data-cursor]").forEach((el) => {
      el.addEventListener("mouseenter", () => {
        root.classList.add("is-hover");
        const key = el.getAttribute("data-cursor");
        if (key && labels[key]) {
          label.textContent = labels[key];
          root.classList.add("has-label");
        }
      });
      el.addEventListener("mouseleave", () => {
        root.classList.remove("is-hover", "has-label");
        label.textContent = "";
      });
    });
  }

  function initMagnetic() {
    if (touch) return;
    document.querySelectorAll(".magnetic").forEach((el) => {
      el.addEventListener("mousemove", (e) => {
        const r = el.getBoundingClientRect();
        const x = e.clientX - (r.left + r.width / 2);
        const y = e.clientY - (r.top + r.height / 2);
        gsap.to(el, { x: x * 0.25, y: y * 0.35, duration: 0.4, ease: "power3.out" });
      });
      el.addEventListener("mouseleave", () => {
        gsap.to(el, { x: 0, y: 0, duration: 0.7, ease: "elastic.out(1,0.4)" });
      });
    });
  }

  function initMenu(lenis) {
    const btn = document.getElementById("menuBtn");
    const menu = document.getElementById("menu");
    let open = false;

    const set = (state) => {
      open = state;
      btn.classList.toggle("is-open", open);
      menu.classList.toggle("is-open", open);
      menu.hidden = !open;
      btn.setAttribute("aria-label", open ? "Close menu" : "Open menu");
      if (lenis) {
        open ? lenis.stop() : lenis.start();
      }
    };

    btn.addEventListener("click", () => set(!open));
    menu.querySelectorAll("a").forEach((a) => {
      a.addEventListener("click", () => set(false));
    });
  }

  function initClock() {
    const el = document.getElementById("heroClock");
    const tick = () => {
      const t = new Date().toLocaleTimeString("en-GB", {
        timeZone: "Europe/Zurich",
        hour12: false,
      });
      el.textContent = `Genève ${t}`;
    };
    tick();
    setInterval(tick, 1000);
  }

  function initForm() {
    const form = document.getElementById("inviteForm");
    const done = document.getElementById("inviteDone");
    form.addEventListener("submit", (e) => {
      e.preventDefault();
      form.hidden = true;
      done.hidden = false;
    });
  }

  function initHeroParallax() {
    gsap.to(".hero-still", {
      yPercent: 12,
      ease: "none",
      scrollTrigger: {
        trigger: "#hero",
        start: "top top",
        end: "bottom top",
        scrub: true,
      },
    });
  }

  boot();
})();
