use aes_gcm::{
    Aes256Gcm, KeyInit, Nonce,
    aead::{Aead, Payload},
};
use anyhow::{Result, ensure};
use base64::{Engine, engine::general_purpose::STANDARD as B64};
use hmac::{Hmac, Mac};
use p256::{
    PublicKey, SecretKey,
    ecdsa::{
        Signature, SigningKey, VerifyingKey,
        signature::{Signer, Verifier},
    },
    pkcs8::{DecodePrivateKey, DecodePublicKey, EncodePrivateKey, EncodePublicKey},
};
use rand::{RngCore, rngs::OsRng};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use zeroize::Zeroizing;
pub fn now() -> i64 {
    chrono::Utc::now().timestamp()
}
pub fn millis() -> i64 {
    chrono::Utc::now().timestamp_millis()
}
pub fn random(n: usize) -> Vec<u8> {
    let mut v = vec![0; n];
    OsRng.fill_bytes(&mut v);
    v
}
pub fn id() -> String {
    hex::encode(random(16))
}
pub fn valid_id(s: &str) -> Result<()> {
    ensure!(
        s.len() == 32
            && s.bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "标识无效"
    );
    Ok(())
}
pub fn b64(b: impl AsRef<[u8]>) -> String {
    B64.encode(b)
}
pub fn un64(s: &str) -> Result<Vec<u8>> {
    ensure!(s.len() <= 600_000, "消息过大");
    Ok(B64.decode(s)?)
}
pub fn hash(b: impl AsRef<[u8]>) -> String {
    hex::encode(Sha256::digest(b.as_ref()))
}
pub fn mac(key: &[u8], data: &[u8]) -> Vec<u8> {
    let mut h = <Hmac<Sha256> as Mac>::new_from_slice(key).unwrap();
    h.update(data);
    h.finalize().into_bytes().to_vec()
}
pub fn equal(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b).fold(0u8, |v, (a, b)| v | (a ^ b)) == 0
}
pub fn kdf(key: &[u8], salt: &[u8], info: &[u8]) -> Result<Vec<u8>> {
    let mut out = vec![0; 32];
    hkdf::Hkdf::<Sha256>::new(Some(salt), key)
        .expand(info, &mut out)
        .map_err(|_| anyhow::anyhow!("密钥派生失败"))?;
    Ok(out)
}
pub fn encrypt(key: &[u8], nonce: &[u8], aad: &[u8], plain: &[u8]) -> Result<Vec<u8>> {
    ensure!(key.len() == 32 && nonce.len() == 12, "密钥或随机数无效");
    Aes256Gcm::new_from_slice(key)
        .unwrap()
        .encrypt(Nonce::from_slice(nonce), Payload { msg: plain, aad })
        .map_err(|_| anyhow::anyhow!("加密失败"))
}
pub fn decrypt(key: &[u8], nonce: &[u8], aad: &[u8], cipher: &[u8]) -> Result<Vec<u8>> {
    ensure!(
        key.len() == 32 && nonce.len() == 12 && cipher.len() >= 16,
        "密文无效"
    );
    Aes256Gcm::new_from_slice(key)
        .unwrap()
        .decrypt(Nonce::from_slice(nonce), Payload { msg: cipher, aad })
        .map_err(|_| anyhow::anyhow!("消息认证失败"))
}
#[derive(Clone, Serialize, Deserialize, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Member {
    pub id: String,
    pub name: String,
    pub signing_key: String,
    pub agreement_key: String,
}
impl Member {
    pub fn validate(&self) -> Result<()> {
        valid_id(&self.id)?;
        ensure!(
            !self.name.trim().is_empty()
                && self.name.chars().count() <= 80
                && !self.name.chars().any(char::is_control),
            "设备名无效"
        );
        VerifyingKey::from_public_key_der(&un64(&self.signing_key)?)?;
        PublicKey::from_public_key_der(&un64(&self.agreement_key)?)?;
        Ok(())
    }
    pub fn verify(&self, data: &[u8], signature: &str) -> Result<()> {
        self.validate()?;
        VerifyingKey::from_public_key_der(&un64(&self.signing_key)?)?
            .verify(data, &Signature::from_der(&un64(signature)?)?)?;
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Identity {
    pub member: Member,
    pub signing_private: String,
    pub agreement_private: String,
}
impl Identity {
    pub fn new(name: &str) -> Result<Self> {
        let s = SigningKey::random(&mut OsRng);
        let a = SecretKey::random(&mut OsRng);
        let own = Self {
            member: Member {
                id: id(),
                name: name.to_owned(),
                signing_key: b64(s.verifying_key().to_public_key_der()?.as_bytes()),
                agreement_key: b64(a.public_key().to_public_key_der()?.as_bytes()),
            },
            signing_private: b64(s.to_pkcs8_der()?.as_bytes()),
            agreement_private: b64(a.to_pkcs8_der()?.as_bytes()),
        };
        own.member.validate()?;
        Ok(own)
    }
    pub fn sign(&self, data: &[u8]) -> Result<String> {
        let private = Zeroizing::new(un64(&self.signing_private)?);
        let key = SigningKey::from_pkcs8_der(&private)?;
        let signature: Signature = key.sign(data);
        Ok(b64(signature.to_der().as_bytes()))
    }
    pub fn agree(&self, peer: &Member) -> Result<Vec<u8>> {
        peer.validate()?;
        let private = Zeroizing::new(un64(&self.agreement_private)?);
        let key = SecretKey::from_pkcs8_der(&private)?;
        let other = PublicKey::from_public_key_der(&un64(&peer.agreement_key)?)?;
        Ok(
            p256::ecdh::diffie_hellman(key.to_nonzero_scalar(), other.as_affine())
                .raw_secret_bytes()
                .to_vec(),
        )
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Config {
    pub server: String,
    pub pair: String,
    pub device: String,
    pub peer: String,
    pub token: String,
    pub key: String,
    pub transport: String,
    pub username: String,
    pub password: String,
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Envelope {
    pub version: u32,
    pub pair: String,
    pub from: String,
    pub to: String,
    pub id: String,
    pub clock: i64,
    pub nonce: String,
    pub data: String,
}
impl Envelope {
    pub fn header(&self) -> Result<Vec<u8>> {
        for s in [&self.pair, &self.from, &self.to, &self.id] {
            valid_id(s)?
        }
        ensure!(
            self.version == 1
                && self.from != self.to
                && (1..=9_007_199_254_740_991).contains(&self.clock),
            "消息头无效"
        );
        Ok(format!(
            "clipbridge:1:{}:{}:{}:{}:{}",
            self.pair, self.from, self.to, self.id, self.clock
        )
        .into_bytes())
    }
    pub fn seal(c: &Config, clock: i64, plain: &impl Serialize) -> Result<Self> {
        let mut e = Self {
            version: 1,
            pair: c.pair.clone(),
            from: c.device.clone(),
            to: c.peer.clone(),
            id: id(),
            clock,
            nonce: b64(random(12)),
            data: String::new(),
        };
        let aad = e.header()?;
        let key = Zeroizing::new(mac(&un64(&c.key)?, &aad));
        e.data = b64(encrypt(
            &key,
            &un64(&e.nonce)?,
            &aad,
            &serde_json::to_vec(plain)?,
        )?);
        Ok(e)
    }
    pub fn open<T: serde::de::DeserializeOwned>(&self, c: &Config) -> Result<T> {
        ensure!(
            self.pair == c.pair && self.from == c.peer && self.to == c.device,
            "消息接收方不匹配"
        );
        let aad = self.header()?;
        let cipher = un64(&self.data)?;
        ensure!(cipher.len() <= 400_000, "密文过大");
        let key = Zeroizing::new(mac(&un64(&c.key)?, &aad));
        let plain = Zeroizing::new(decrypt(&key, &un64(&self.nonce)?, &aad, &cipher)?);
        Ok(serde_json::from_slice(&plain)?)
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Text {
    pub kind: String,
    pub text: String,
    pub created: i64,
    pub expires: i64,
    #[serde(default)]
    pub event_id: String,
    #[serde(default)]
    pub origin: String,
    #[serde(default)]
    pub event_clock: i64,
}
impl Text {
    pub fn validate(&self) -> Result<()> {
        ensure!(
            self.kind == "text"
                && self.text.len() <= 65536
                && self.created <= now() + 60
                && self.expires > now()
                && self.expires >= self.created
                && self.expires - self.created <= 600,
            "文本过期或无效"
        );
        valid_id(&self.event_id)?;
        valid_id(&self.origin)?;
        ensure!(self.event_clock > 0, "事件序号无效");
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct Boxed {
    pub nonce: String,
    pub data: String,
}
impl Boxed {
    pub fn seal(key: &[u8], aad: &str, plain: &impl Serialize) -> Result<Self> {
        let nonce = random(12);
        Ok(Self {
            data: b64(encrypt(
                key,
                &nonce,
                aad.as_bytes(),
                &serde_json::to_vec(plain)?,
            )?),
            nonce: b64(nonce),
        })
    }
    pub fn open<T: serde::de::DeserializeOwned>(&self, key: &[u8], aad: &str) -> Result<T> {
        let c = un64(&self.data)?;
        ensure!(c.len() <= 200_000, "授权消息过大");
        Ok(serde_json::from_slice(&decrypt(
            key,
            &un64(&self.nonce)?,
            aad.as_bytes(),
            &c,
        )?)?)
    }
}
pub const CHUNK: usize = 256 * 1024;
pub const MAX_FILE: u64 = 100 * 1024 * 1024;
pub fn chunk(c: &Config, id: &str, part: usize, data: &[u8], seal: bool) -> Result<Vec<u8>> {
    valid_id(id)?;
    ensure!(part < 400, "附件块编号无效");
    let (from, to) = if seal {
        (&c.device, &c.peer)
    } else {
        (&c.peer, &c.device)
    };
    let aad = format!("clipbridge:attachment:1:{}:{from}:{to}:{id}:{part}", c.pair);
    let key = Zeroizing::new(mac(&un64(&c.key)?, aad.as_bytes()));
    if seal {
        ensure!(data.len() <= CHUNK, "附件块过大");
        let mut nonce = random(12);
        nonce.extend(encrypt(&key, &nonce, aad.as_bytes(), data)?);
        Ok(nonce)
    } else {
        ensure!((28..=CHUNK + 28).contains(&data.len()), "附件块长度无效");
        decrypt(&key, &data[..12], aad.as_bytes(), &data[12..])
    }
}
pub fn safe_name(s: &str) -> String {
    let mut n: String = s
        .chars()
        .map(|c| {
            if c.is_control() || "<>:\"/\\|?*".contains(c) {
                '_'
            } else {
                c
            }
        })
        .take(160)
        .collect();
    n = n.trim_matches([' ', '.']).to_owned();
    if n.is_empty() {
        n = "file".into()
    }
    let stem = n.split('.').next().unwrap_or("").to_ascii_uppercase();
    if [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
        "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ]
    .contains(&stem.as_str())
    {
        n.insert(0, '_')
    }
    n
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn authenticated_text_and_chunks() {
        let a = Identity::new("电脑").unwrap();
        let b = Identity::new("手机").unwrap();
        assert_eq!(a.agree(&b.member).unwrap(), b.agree(&a.member).unwrap());
        let c = Config {
            server: "https://example.com".into(),
            pair: id(),
            device: id(),
            peer: id(),
            token: b64(random(32)),
            key: b64(random(32)),
            transport: "webdav".into(),
            username: "a".into(),
            password: "b".into(),
        };
        let peer = Config {
            device: c.peer.clone(),
            peer: c.device.clone(),
            ..c.clone()
        };
        let e = Envelope::seal(&c, 1, &serde_json::json!({"text":"中文"})).unwrap();
        assert_eq!(e.open::<serde_json::Value>(&peer).unwrap()["text"], "中文");
        let mut bad = e.clone();
        bad.id = id();
        assert!(bad.open::<serde_json::Value>(&peer).is_err());
        for size in [0, 1, CHUNK] {
            let bytes = vec![42; size];
            let enc = chunk(&c, &c.pair, 0, &bytes, true).unwrap();
            assert_eq!(chunk(&peer, &c.pair, 0, &enc, false).unwrap(), bytes);
            assert!(chunk(&peer, &c.pair, 1, &enc, false).is_err())
        }
    }
}
