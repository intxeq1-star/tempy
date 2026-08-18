# Maison Vérault

An invitation-only luxury maison website — the kind of exclusive, cinematic house site you see from haute horlogerie and quiet-luxury houses today.

Scroll, and a full-screen film plays. That is the point.

**Vérault** is fictional. The craft is not.

## What you get

- Cinematic preloader with a counted entrance
- Full-bleed hero, gold-on-void, living still
- **Scroll-driven film** — Apple-style scrollytelling. The frame is pinned; your scroll is the playhead
- Custom gold cursor, magnetic buttons, split-title reveal
- Editorial collection, atelier story, private salon letter
- Smooth scroll (Lenis) and timeline motion (GSAP + ScrollTrigger)
- Under 5 MB. Well under the 120 MB ceiling

## The film

Luxury product pages (Apple, Patek-adjacent houses, Awwwards maisons) rarely “play a video” in the ordinary sense. They bind a sequence of cinematic frames to scroll position.

Vérault does the same:

1. Seven campaign stills are preloaded
2. A sticky canvas fills the viewport for several screens of scroll
3. Progress crossfades and Ken-Burns the next frame
4. Chapter titles — *Soie*, *L’Atelier*, *Orion* — change with the reel

It feels like a private film because it is timed to your hand.

## Run it

Open `index.html` locally, or from the repo root:

```bash
python3 -m http.server 8080
```

Then visit `http://localhost:8080`.

## Stack

Vanilla HTML, CSS, and JavaScript. No build step.

| Piece | Why |
| --- | --- |
| [GSAP](https://gsap.com/) + ScrollTrigger | Precise scroll timelines |
| [Lenis](https://github.com/darkroomengineering/lenis) | Inertial smooth scroll |
| Cinzel, Cormorant Garamond, Outfit | Display / editorial / UI |

## Design notes

Researched against current luxury-web practice: dark charcoal rather than flat black, antique gold, oversized serif headlines, almost no chrome, grain, letterbox, and motion that is slow on purpose. Quiet luxury, not a nightclub.

## License

Site code is yours to study and reuse. Imagery is original campaign stills created for this maison. Vérault is a fictional house.
