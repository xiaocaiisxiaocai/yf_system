use rand::Rng;
use sha2::{Digest, Sha256};

/// 生成不透明 refresh token（hex(随机 32 字节)），库中只存其 sha256
pub fn new_refresh_token() -> String {
    let bytes: [u8; 32] = rand::thread_rng().gen();
    hex::encode(bytes)
}

pub fn hash_token(token: &str) -> String {
    hex::encode(Sha256::digest(token.as_bytes()))
}
