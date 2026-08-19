(() => {
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const touch = window.matchMedia("(hover: none), (pointer: coarse)").matches;

  const defaultPreload = [
    "assets/images/hero-still.jpg",
    "assets/images/watch-orion.jpg",
    "assets/images/watch-nocturne.jpg",
    "assets/images/watch-fall.jpg",
    "assets/images/box-open.jpg",
    "assets/images/box-closed.jpg",
    "assets/images/box-sealed.jpg",
    "assets/images/watch-in-box.jpg",
    "assets/images/atelier.jpg",
    "assets/images/hands.jpg",
    "assets/images/parfum.jpg",
    "assets/images/bijou.jpg",
    "assets/images/portrait.jpg",
    "assets/images/salon.jpg",
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
    const listed = document.body.dataset.preload
      ? document.body.dataset.preload.split(",").map((s) => s.trim())
      : defaultPreload;

    if (loader && countEl) {
      let done = 0;
      await Promise.all(
        listed.map(async (src) => {
          await loadImage(src);
          done += 1;
          const p = (done / listed.length) * 100;
          countEl.textContent = pad(p);
          if (bar) bar.style.width = `${p}%`;
        })
      );
      await new Promise((r) => setTimeout(r, 220));
      loader.style.transition = "opacity 0.85s cubic-bezier(0.16,1,0.3,1), visibility 0.85s";
      loader.style.opacity = "0";
      loader.style.visibility = "hidden";
    }

    document.body.classList.add("is-ready");
    initChrome();
  }

  function initChrome() {
    if (window.gsap && window.ScrollTrigger) {
      gsap.registerPlugin(ScrollTrigger);
    }

    let lenis = null;
    if (!reduce && window.Lenis) {
      lenis = new Lenis({
        duration: 1.15,
        smoothWheel: true,
        wheelMultiplier: 0.9,
      });
      if (window.ScrollTrigger) {
        lenis.on("scroll", ScrollTrigger.update);
        gsap.ticker.add((time) => lenis.raf(time * 1000));
        gsap.ticker.lagSmoothing(0);
      } else {
        const raf = (t) => {
          lenis.raf(t);
          requestAnimationFrame(raf);
        };
        requestAnimationFrame(raf);
      }
    }

    if (window.gsap) {
      gsap.to(".hero-title .char", {
        y: 0,
        duration: 1.25,
        stagger: 0.055,
        ease: "power4.out",
        delay: 0.12,
      });
      gsap.to(".fade-up", {
        opacity: 1,
        y: 0,
        duration: 1.05,
        stagger: 0.1,
        ease: "power3.out",
        delay: 0.35,
      });
      gsap.utils.toArray(".reveal").forEach((el) => {
        gsap.to(el, {
          opacity: 1,
          y: 0,
          duration: 1.1,
          ease: "power3.out",
          scrollTrigger: { trigger: el, start: "top 88%" },
        });
      });
      gsap.utils.toArray(".reveal-img img").forEach((el) => {
        gsap.to(el, {
          scale: 1,
          filter: "saturate(1)",
          duration: 1.5,
          ease: "power3.out",
          scrollTrigger: { trigger: el, start: "top 90%" },
        });
      });
    }

    const progress = document.querySelector(".progress i");
    if (progress && window.ScrollTrigger) {
      ScrollTrigger.create({
        trigger: document.body,
        start: "top top",
        end: "bottom bottom",
        onUpdate: (self) => {
          progress.style.width = `${self.progress * 100}%`;
        },
      });
    }

    initCursor();
    initMagnetic();
    initMenu(lenis);
    initClock();
    initForm();
    initHeroParallax();

    if (typeof window.VeraultPack === "function") window.VeraultPack();
    if (typeof window.VeraultFilm === "function") window.VeraultFilm();
  }

  function initCursor() {
    if (touch) return;
    const root = document.querySelector(".cursor");
    if (!root) return;
    const dot = root.querySelector(".cursor-dot");
    const ring = root.querySelector(".cursor-ring");
    const label = root.querySelector(".cursor-label");
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
    if (touch || !window.gsap) return;
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
    if (!btn || !menu) return;
    let open = false;
    const set = (state) => {
      open = state;
      btn.classList.toggle("is-open", open);
      menu.classList.toggle("is-open", open);
      menu.hidden = !open;
      btn.setAttribute("aria-label", open ? "Close menu" : "Open menu");
      if (lenis) (open ? lenis.stop() : lenis.start());
    };
    btn.addEventListener("click", () => set(!open));
    menu.querySelectorAll("a").forEach((a) => a.addEventListener("click", () => set(false)));
  }

  function initClock() {
    document.querySelectorAll("[data-clock]").forEach((el) => {
      const tick = () => {
        el.textContent = `Genève ${new Date().toLocaleTimeString("en-GB", {
          timeZone: "Europe/Zurich",
          hour12: false,
        })}`;
      };
      tick();
      setInterval(tick, 1000);
    });
  }

  function initForm() {
    const form = document.getElementById("inviteForm");
    const done = document.getElementById("inviteDone");
    if (!form || !done) return;
    form.addEventListener("submit", (e) => {
      e.preventDefault();
      form.hidden = true;
      done.hidden = false;
    });
  }

  function initHeroParallax() {
    if (!window.gsap || !document.querySelector(".hero-still")) return;
    gsap.to(".hero-still", {
      yPercent: 12,
      ease: "none",
      scrollTrigger: { trigger: "#hero", start: "top top", end: "bottom top", scrub: true },
    });
  }

  boot();
})();
