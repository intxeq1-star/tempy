window.VeraultPack = function initPack() {
  const section = document.getElementById("pack");
  if (!section || !window.gsap) return;

  const watch = document.getElementById("orionWatch");
  const lid = document.getElementById("boxLid");
  const ribbon = document.getElementById("boxRibbon");
  const seal = document.getElementById("boxSeal");
  const title = document.getElementById("packTitle");
  const line = document.getElementById("packLine");
  const index = document.getElementById("packIndex");
  const bar = document.getElementById("packBar");
  const stills = Array.from(document.querySelectorAll(".pack-stills i"));
  const world = document.querySelector(".world");

  const chapters = [
    { at: 0, i: "01 / 06", t: "The fall", l: "Orion leaves the lamp and finds the air." },
    { at: 0.16, i: "02 / 06", t: "The hover", l: "A pause. The coffret waits, still closed." },
    { at: 0.32, i: "03 / 06", t: "The opening", l: "The lid lifts. Suede takes the light." },
    { at: 0.5, i: "04 / 06", t: "The nest", l: "The piece finds the well it was made for." },
    { at: 0.68, i: "05 / 06", t: "The close", l: "The house closes what it has finished." },
    { at: 0.84, i: "06 / 06", t: "The seal", l: "Ribbon. Wax. A letter, not a parcel." },
  ];

  const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
  const range = (p, a, b) => clamp((p - a) / (b - a));
  const lerp = (a, b, t) => a + (b - a) * t;
  const ease = (t) => t * t * (3 - 2 * t);

  let last = -1;

  const apply = (p) => {
    const fall = ease(range(p, 0, 0.2));
    const hover = ease(range(p, 0.18, 0.32));
    const open = ease(range(p, 0.32, 0.5));
    const nest = ease(range(p, 0.48, 0.66));
    const close = ease(range(p, 0.66, 0.82));
    const stamp = ease(range(p, 0.82, 0.96));

    const y = lerp(-68, -8, fall);
    const y2 = lerp(y, 42, nest);
    const rotX = lerp(-48, -8, fall);
    const rotX2 = lerp(rotX, 76, nest);
    const rotZ = lerp(28, 6, fall + hover * 0.4);
    const rotY = lerp(-12, 10, fall);
    const scale = lerp(1, 0.58, nest);

    watch.style.transform =
      `translate3d(0, ${y2}vh, ${lerp(90, 20, nest)}px) rotateX(${rotX2}deg) rotateY(${rotY}deg) rotateZ(${rotZ}deg) scale(${scale})`;

    const lidAngle = lerp(0, -124, open) * (1 - close);
    lid.style.transform = `rotateX(${lidAngle}deg)`;

    ribbon.style.width = `${lerp(0, 310, stamp)}px`;
    ribbon.style.marginLeft = `${lerp(0, -155, stamp)}px`;
    ribbon.style.opacity = stamp;

    seal.style.transform = `translateZ(118px) scale(${lerp(0, 1, stamp)})`;
    seal.style.opacity = stamp;

    world.style.transform = `rotateX(${lerp(22, 14, p)}deg) rotateY(${lerp(-22, -6, p)}deg)`;

    const ch = [...chapters].reverse().find((c) => p >= c.at) || chapters[0];
    const ci = chapters.indexOf(ch);
    if (ci !== last) {
      last = ci;
      index.textContent = ch.i;
      title.textContent = ch.t;
      line.textContent = ch.l;
      gsap.fromTo(
        [title, line, index],
        { opacity: 0, y: 16 },
        { opacity: 1, y: 0, duration: 0.55, stagger: 0.05, ease: "power3.out" }
      );
    }

    stills.forEach((el, n) => el.classList.toggle("is-on", n === Math.min(stills.length - 1, ci)));
    if (bar) bar.style.width = `${p * 100}%`;
  };

  apply(0);

  ScrollTrigger.create({
    trigger: section,
    start: "top top",
    end: "bottom bottom",
    onUpdate: (self) => apply(self.progress),
  });
};
