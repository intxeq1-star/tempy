# Maison Vérault

A full luxury **website** — not a single page. An invitation-only maison of time, with a home film and a 3D watch that **falls, then packs itself into a coffret** as you scroll.

**Vérault** is fictional. The rooms are real files.

## The coffret

Scroll the home past the manifesto. Orion drops through the dark, hovers, the lid opens, the watch nests in suede, the lid closes, ribbon and wax seal themselves. Your scroll is the playhead.

Then a second cinematic film continues the house story.

## Rooms of the site

| Page | What it is |
| --- | --- |
| `index.html` | Home — 1000 lines. Hero, fall-and-pack, film, collection, journal, salon |
| `maison.html` | The house, 1891–2026 |
| `collection.html` | Four pieces |
| `orion.html` | Orion 41 |
| `nocturne.html` | Nocturne |
| `ambre.html` | Ambre Noir |
| `sceau.html` | Sceau |
| `atelier.html` | The bench |
| `journal.html` | Notes from the lamp |
| `salon.html` | Private letter |

Styles live in `css/`. Motion lives in `js/`. This is a website: HTML + CSS + JavaScript, many pages, no build step.

## Run it

```bash
python3 -m http.server 8080
```

Open `http://localhost:8080`.

## Stack

Vanilla pages. [GSAP](https://gsap.com/) + ScrollTrigger. [Lenis](https://github.com/darkroomengineering/lenis). Cinzel, Cormorant Garamond, Outfit.

About **5 MB**. Well under 120 MB.

## License

Study and reuse the code. Imagery was made for this maison.
