//! 图形验证码：6 位随机字符，栅格 PNG data URI 返回，5 分钟有效，一次性核销。
//! 存储于 AppState.captchas（单进程部署足够；多实例部署需换 Redis）。
use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use embedded_graphics::{
    geometry::{OriginDimensions, Size},
    mono_font::{ascii::FONT_10X20, MonoTextStyle},
    pixelcolor::BinaryColor,
    prelude::{DrawTarget, Drawable, Pixel, Point},
    text::{Baseline, Text},
};
use rand::{seq::SliceRandom, Rng};
use std::{
    convert::Infallible,
    time::{Duration, Instant},
};

use crate::state::CaptchaStore;

const TTL: Duration = Duration::from_secs(300);
const MAX_CHALLENGES: usize = 4096;
const WIDTH: u32 = 192;
const HEIGHT: u32 = 60;
const CAPTCHA_LEN: usize = 6;

struct GlyphMask {
    pixels: [bool; 10 * 20],
}

impl GlyphMask {
    fn render(character: char) -> Self {
        let mut mask = Self {
            pixels: [false; 10 * 20],
        };
        let style = MonoTextStyle::new(&FONT_10X20, BinaryColor::On);
        Text::with_baseline(&character.to_string(), Point::zero(), style, Baseline::Top)
            .draw(&mut mask)
            .expect("in-memory glyph drawing cannot fail");
        mask
    }

    fn is_set(&self, x: usize, y: usize) -> bool {
        self.pixels[y * 10 + x]
    }
}

impl OriginDimensions for GlyphMask {
    fn size(&self) -> Size {
        Size::new(10, 20)
    }
}

impl DrawTarget for GlyphMask {
    type Color = BinaryColor;
    type Error = Infallible;

    fn draw_iter<I>(&mut self, pixels: I) -> Result<(), Self::Error>
    where
        I: IntoIterator<Item = Pixel<Self::Color>>,
    {
        for Pixel(point, color) in pixels {
            if color == BinaryColor::On
                && point.x >= 0
                && point.y >= 0
                && point.x < 10
                && point.y < 20
            {
                self.pixels[point.y as usize * 10 + point.x as usize] = true;
            }
        }
        Ok(())
    }
}

struct CaptchaImage {
    pixels: Vec<u8>,
}

impl CaptchaImage {
    fn new(rng: &mut impl Rng) -> Self {
        let background = [
            rng.gen_range(232..=248),
            rng.gen_range(236..=250),
            rng.gen_range(232..=248),
        ];
        Self {
            pixels: background.repeat((WIDTH * HEIGHT) as usize),
        }
    }

    fn put(&mut self, x: i32, y: i32, color: [u8; 3]) {
        if x < 0 || y < 0 || x >= WIDTH as i32 || y >= HEIGHT as i32 {
            return;
        }
        let offset = ((y as u32 * WIDTH + x as u32) * 3) as usize;
        self.pixels[offset..offset + 3].copy_from_slice(&color);
    }

    fn line(&mut self, start: (i32, i32), end: (i32, i32), color: [u8; 3]) {
        let (mut x0, mut y0) = start;
        let (x1, y1) = end;
        let dx = (x1 - x0).abs();
        let sx = if x0 < x1 { 1 } else { -1 };
        let dy = -(y1 - y0).abs();
        let sy = if y0 < y1 { 1 } else { -1 };
        let mut error = dx + dy;
        loop {
            self.put(x0, y0, color);
            if x0 == x1 && y0 == y1 {
                break;
            }
            let doubled = 2 * error;
            if doubled >= dy {
                error += dy;
                x0 += sx;
            }
            if doubled <= dx {
                error += dx;
                y0 += sy;
            }
        }
    }

    fn draw_glyph(&mut self, glyph: &GlyphMask, left: i32, top: i32, phase: i32, color: [u8; 3]) {
        for source_y in 0..20 {
            let wave = ((source_y as i32 + phase) % 7 - 3) / 2;
            for source_x in 0..10 {
                if !glyph.is_set(source_x, source_y) {
                    continue;
                }
                let x = left + source_x as i32 * 2 + wave;
                let y = top + source_y as i32 * 2;
                for offset_y in 0..2 {
                    for offset_x in 0..2 {
                        self.put(x + offset_x, y + offset_y, color);
                    }
                }
            }
        }
    }

    fn encode_png(&self) -> Vec<u8> {
        let mut encoded = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut encoded, WIDTH, HEIGHT);
            encoder.set_color(png::ColorType::Rgb);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder
                .write_header()
                .expect("writing a PNG header to memory cannot fail");
            writer
                .write_image_data(&self.pixels)
                .expect("writing PNG data to memory cannot fail");
        }
        encoded
    }
}

fn generate() -> (String, String) {
    const CHARS: &[u8] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    let mut rng = rand::thread_rng();
    let code: String = (0..CAPTCHA_LEN)
        .map(|_| *CHARS.choose(&mut rng).expect("character set is nonempty") as char)
        .collect();
    let mut image = CaptchaImage::new(&mut rng);

    for (index, character) in code.chars().enumerate() {
        let color = [
            rng.gen_range(18..=78),
            rng.gen_range(24..=82),
            rng.gen_range(38..=96),
        ];
        image.draw_glyph(
            &GlyphMask::render(character),
            7 + index as i32 * 30 + rng.gen_range(-2..=2),
            rng.gen_range(7..=11),
            rng.gen_range(0..7),
            color,
        );
    }

    for _ in 0..4 {
        image.line(
            (0, rng.gen_range(3..HEIGHT as i32 - 3)),
            (WIDTH as i32 - 1, rng.gen_range(3..HEIGHT as i32 - 3)),
            [
                rng.gen_range(100..=175),
                rng.gen_range(100..=175),
                rng.gen_range(100..=175),
            ],
        );
    }
    for _ in 0..180 {
        image.put(
            rng.gen_range(0..WIDTH as i32),
            rng.gen_range(0..HEIGHT as i32),
            [
                rng.gen_range(90..=190),
                rng.gen_range(90..=190),
                rng.gen_range(90..=190),
            ],
        );
    }

    let data_uri = format!(
        "data:image/png;base64,{}",
        BASE64.encode(image.encode_png())
    );
    (code, data_uri)
}

pub fn issue(store: &CaptchaStore) -> (String, String) {
    let (code, image) = generate();
    let id = uuid::Uuid::new_v4().to_string();

    let mut map = store.lock().unwrap();
    map.retain(|_, (_, exp)| *exp > Instant::now());
    if map.len() >= MAX_CHALLENGES {
        // Public issuance cannot grow memory without bound; evict the oldest challenge.
        if let Some(oldest) = map
            .iter()
            .min_by_key(|(_, (_, expiry))| *expiry)
            .map(|(key, _)| key.clone())
        {
            map.remove(&oldest);
        }
    }
    map.insert(id.clone(), (code.clone(), Instant::now() + TTL));
    drop(map);

    (id, image)
}

/// 核销（一次性）
pub fn verify(store: &CaptchaStore, id: &str, code: &str) -> bool {
    let mut map = store.lock().unwrap();
    match map.remove(id) {
        Some((answer, exp)) => exp > Instant::now() && answer.eq_ignore_ascii_case(code.trim()),
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn captcha_store_has_a_fixed_capacity_and_keeps_the_new_challenge() {
        let store = CaptchaStore::default();
        let expiry = Instant::now() + TTL;
        for index in 0..4096 {
            store
                .lock()
                .unwrap()
                .insert(format!("old-{index}"), ("ABCD".into(), expiry));
        }
        let (id, _) = issue(&store);
        assert_eq!(store.lock().unwrap().len(), 4096);
        let code = store.lock().unwrap()[&id].0.clone();
        assert!(verify(&store, &id, &code));
    }

    #[test]
    fn captcha_is_single_use_case_insensitive_and_expires() {
        let store = CaptchaStore::default();
        let (id, image) = issue(&store);
        assert!(image.starts_with("data:image/png;base64,"));
        assert!(!image.contains("<svg"));
        let png = BASE64
            .decode(image.strip_prefix("data:image/png;base64,").unwrap())
            .unwrap();
        assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
        assert_eq!(u32::from_be_bytes(png[16..20].try_into().unwrap()), WIDTH);
        assert_eq!(u32::from_be_bytes(png[20..24].try_into().unwrap()), HEIGHT);
        let mut offset = 8;
        while offset + 12 <= png.len() {
            let length = u32::from_be_bytes(png[offset..offset + 4].try_into().unwrap()) as usize;
            let chunk_type = &png[offset + 4..offset + 8];
            assert!(!matches!(chunk_type, b"tEXt" | b"zTXt" | b"iTXt"));
            offset += length + 12;
        }
        let code = store.lock().unwrap()[&id].0.clone();
        assert_eq!(code.len(), CAPTCHA_LEN);
        assert!(code
            .bytes()
            .all(|character| b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789".contains(&character)));
        assert!(verify(&store, &id, &format!(" {} ", code.to_lowercase())));
        assert!(!verify(&store, &id, &code));
        let (wrong, _) = issue(&store);
        assert!(!verify(&store, &wrong, "wrong"));
        assert!(!store.lock().unwrap().contains_key(&wrong));
        store.lock().unwrap().insert(
            "expired".into(),
            ("ABCD".into(), Instant::now() - Duration::from_secs(1)),
        );
        assert!(!verify(&store, "expired", "ABCD"));
    }
}
