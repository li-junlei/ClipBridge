use crate::{crypto::*, jpake_constants as constants};
use anyhow::{Result, ensure};
use num_bigint::{BigInt, BigUint, RandBigInt};
use num_traits::{One, Zero};
use rand::rngs::OsRng;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use unicode_normalization::UnicodeNormalization;
const ALPHABET: &[u8] = b"23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct Message {
    pub round: u32,
    pub id: String,
    pub values: Vec<String>,
}
pub fn normalize(s: &str) -> Result<String> {
    let out:String=s.nfkc().flat_map(char::to_uppercase).filter(|c|!c.is_whitespace()&&!matches!(*c,'-'|'\u{2010}'..='\u{2015}'|'\u{2212}'|'\u{200b}'..='\u{200f}'|'\u{feff}'|'\u{2060}')).collect();
    ensure!(
        out.len() == 16 && out.bytes().all(|b| ALPHABET.contains(&b)),
        "配对码应为 16 位字母或数字"
    );
    Ok(out)
}
pub fn new_code() -> String {
    random(16)
        .iter()
        .map(|b| ALPHABET[(b % 32) as usize] as char)
        .collect()
}
pub fn display(code: &str) -> String {
    code.as_bytes()
        .chunks(4)
        .map(|s| std::str::from_utf8(s).unwrap_or(""))
        .collect::<Vec<_>>()
        .join("-")
}
fn num(s: &str) -> Result<BigUint> {
    ensure!(
        !s.is_empty()
            && s.len() <= 800
            && s.bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "握手整数无效"
    );
    BigUint::parse_bytes(s.as_bytes(), 16).ok_or_else(|| anyhow::anyhow!("握手整数无效"))
}
fn mod_signed(h: BigInt, q: &BigUint) -> BigUint {
    let q = BigInt::from(q.clone());
    (((h % &q) + &q) % &q).to_biguint().unwrap()
}
fn challenge(base: &BigUint, commit: &BigUint, public: &BigUint, id: &str, q: &BigUint) -> BigUint {
    let mut h = Sha256::new();
    for bytes in [
        base.to_bytes_be(),
        commit.to_bytes_be(),
        public.to_bytes_be(),
        id.as_bytes().to_vec(),
    ] {
        h.update((bytes.len() as u32).to_be_bytes());
        h.update(bytes)
    }
    mod_signed(BigInt::from_signed_bytes_be(&h.finalize()), q)
}
pub fn commitment(m: &Member, group: &str, root: &str) -> String {
    format!(
        "{group}\n{root}\n{}\n{}\n{}\n{}",
        m.id,
        b64(&m.name),
        m.signing_key,
        m.agreement_key
    )
}

// Fixed-width exponentiation avoids an exponent-length-dependent PAKE loop.
trait PakePower {
    fn modpow_ct(&self, exponent: &BigUint, modulus: &BigUint) -> BigUint;
}
impl PakePower for BigUint {
    fn modpow_ct(&self, exponent: &BigUint, _modulus: &BigUint) -> BigUint {
        use p256::elliptic_curve::bigint::{
            Encoding, U256, U3072,
            modular::runtime_mod::{DynResidue, DynResidueParams},
        };
        static PARAMETERS: std::sync::OnceLock<
            DynResidueParams<{ 3072 / p256::elliptic_curve::bigint::Limb::BITS }>,
        > = std::sync::OnceLock::new();
        let params =
            PARAMETERS.get_or_init(|| DynResidueParams::new(&U3072::from_be_hex(constants::P)));
        let value = self.to_bytes_be();
        let exp = exponent.to_bytes_be();
        assert!(value.len() <= 384 && exp.len() <= 32);
        let mut base = [0u8; 384];
        let mut power = [0u8; 32];
        base[384 - value.len()..].copy_from_slice(&value);
        power[32 - exp.len()..].copy_from_slice(&exp);
        let result = DynResidue::new(&U3072::from_be_slice(&base), *params)
            .pow(&U256::from_be_slice(&power))
            .retrieve();
        use zeroize::Zeroize;
        power.zeroize();
        BigUint::from_bytes_be(&result.to_be_bytes())
    }
}
pub struct Pake {
    p: BigUint,
    q: BigUint,
    g: BigUint,
    x1: BigUint,
    x2: BigUint,
    s: BigUint,
    gx1: BigUint,
    gx2: BigUint,
    gx3: BigUint,
    gx4: BigUint,
    material: BigUint,
    pub master: Vec<u8>,
    own: String,
    peer: String,
    role: &'static str,
    locator: String,
    pub commit_nonce: String,
    stage: u32,
}
impl Pake {
    pub fn new(code: &str, host: bool, commitment: &str) -> Result<Self> {
        let code = normalize(code)?;
        let locator = code[..6].to_owned();
        let role = if host { "a" } else { "b" };
        let nonce = b64(random(32));
        let own = format!(
            "{role}:{locator}:{}",
            &hash(format!(
                "clipbridge:group-commit:1:{locator}:{role}:{nonce}:{commitment}"
            ))[..32]
        );
        let p = num(&constants::P.to_ascii_lowercase())?;
        let q = num(&constants::Q.to_ascii_lowercase())?;
        let g = num(&constants::G.to_ascii_lowercase())?;
        let x1 = OsRng.gen_biguint_below(&q);
        let x2 = OsRng.gen_biguint_range(&BigUint::one(), &q);
        let s = BigUint::from_bytes_be(code[6..].as_bytes()) % &q;
        let gx1 = g.modpow_ct(&x1, &p);
        let gx2 = g.modpow_ct(&x2, &p);
        Ok(Self {
            p,
            q,
            g,
            x1,
            x2,
            s,
            gx1,
            gx2,
            gx3: BigUint::zero(),
            gx4: BigUint::zero(),
            material: BigUint::zero(),
            master: vec![],
            own,
            peer: String::new(),
            role,
            locator,
            commit_nonce: nonce,
            stage: 0,
        })
    }
    fn proof(&self, base: &BigUint, public: &BigUint, secret: &BigUint) -> [String; 2] {
        let v = OsRng.gen_biguint_below(&self.q);
        let gv = base.modpow_ct(&v, &self.p);
        let h = challenge(base, &gv, public, &self.own, &self.q);
        let r = mod_signed(BigInt::from(v) - BigInt::from(secret * &h), &self.q);
        [gv.to_str_radix(16), r.to_str_radix(16)]
    }
    fn verify(
        &self,
        base: &BigUint,
        public: &BigUint,
        values: &[String],
        peer: &str,
    ) -> Result<()> {
        ensure!(
            values.len() == 2
                && !public.is_zero()
                && public < &self.p
                && public.modpow_ct(&self.q, &self.p) == BigUint::one(),
            "握手群元素无效"
        );
        let gv = num(&values[0])?;
        let r = num(&values[1])?;
        ensure!(gv < self.p && r < self.q, "握手证明范围无效");
        let h = challenge(base, &gv, public, peer, &self.q);
        ensure!(
            (base.modpow_ct(&r, &self.p) * public.modpow_ct(&h, &self.p)) % &self.p == gv,
            "配对证明无效"
        );
        Ok(())
    }
    fn check(&mut self, m: &Message, round: u32, count: usize) -> Result<()> {
        let prefix = format!(
            "{}:{}:",
            if self.role == "a" { "b" } else { "a" },
            self.locator
        );
        ensure!(
            m.round == round
                && m.values.len() == count
                && m.id.len() == prefix.len() + 32
                && m.id.starts_with(&prefix)
                && (self.peer.is_empty() || self.peer == m.id),
            "配对握手无效"
        );
        valid_id(&m.id[prefix.len()..])?;
        self.peer = m.id.clone();
        Ok(())
    }
    pub fn round1(&mut self) -> Result<Message> {
        ensure!(self.stage == 0, "握手阶段无效");
        self.stage = 1;
        let mut values = vec![self.gx1.to_str_radix(16), self.gx2.to_str_radix(16)];
        values.extend(self.proof(&self.g, &self.gx1, &self.x1));
        values.extend(self.proof(&self.g, &self.gx2, &self.x2));
        Ok(Message {
            round: 1,
            id: self.own.clone(),
            values,
        })
    }
    pub fn round2(&mut self, m: &Message) -> Result<Message> {
        ensure!(self.stage == 1, "握手阶段无效");
        self.check(m, 1, 6)?;
        let gx3 = num(&m.values[0])?;
        let gx4 = num(&m.values[1])?;
        ensure!(gx4 != BigUint::one(), "无效握手公钥");
        self.verify(&self.g, &gx3, &m.values[2..4], &m.id)?;
        self.verify(&self.g, &gx4, &m.values[4..6], &m.id)?;
        self.gx3 = gx3;
        self.gx4 = gx4;
        let base = (&self.gx1 * &self.gx3 * &self.gx4) % &self.p;
        ensure!(base != BigUint::one(), "无效握手生成元");
        let secret = (&self.x2 * &self.s) % &self.q;
        let a = base.modpow_ct(&secret, &self.p);
        let mut values = vec![a.to_str_radix(16)];
        values.extend(self.proof(&base, &a, &secret));
        self.stage = 2;
        Ok(Message {
            round: 2,
            id: self.own.clone(),
            values,
        })
    }
    fn tag(&self, reverse: bool) -> Vec<u8> {
        let mut material = self.material.to_bytes_be();
        material.extend_from_slice(b"JPAKE_KC");
        let key = Sha256::digest(material);
        let (a, b, values) = if reverse {
            (
                &self.peer,
                &self.own,
                [&self.gx3, &self.gx4, &self.gx1, &self.gx2],
            )
        } else {
            (
                &self.own,
                &self.peer,
                [&self.gx1, &self.gx2, &self.gx3, &self.gx4],
            )
        };
        let mut input = format!("KC_1_U{a}{b}").into_bytes();
        for v in values {
            input.extend(v.to_bytes_be())
        }
        mac(&key, &input)
    }
    pub fn round3(&mut self, m: &Message) -> Result<Message> {
        ensure!(self.stage == 2, "握手阶段无效");
        self.check(m, 2, 3)?;
        let b = num(&m.values[0])?;
        let base = (&self.gx3 * &self.gx1 * &self.gx2) % &self.p;
        ensure!(base != BigUint::one(), "无效握手生成元");
        self.verify(&base, &b, &m.values[1..], &m.id)?;
        let exponent = mod_signed(-BigInt::from(&self.x2 * &self.s), &self.q);
        self.material =
            ((self.gx4.modpow_ct(&exponent, &self.p) * b) % &self.p).modpow_ct(&self.x2, &self.p);
        self.stage = 3;
        Ok(Message {
            round: 3,
            id: self.own.clone(),
            values: vec![BigInt::from_signed_bytes_be(&self.tag(false)).to_str_radix(16)],
        })
    }
    pub fn finish(&mut self, m: &Message) -> Result<()> {
        ensure!(self.stage == 3, "握手阶段无效");
        self.check(m, 3, 1)?;
        ensure!(m.values[0].len() <= 66, "配对认证无效");
        let tag = BigInt::parse_bytes(m.values[0].as_bytes(), 16)
            .ok_or_else(|| anyhow::anyhow!("配对认证无效"))?;
        ensure!(
            equal(
                &tag.to_signed_bytes_be(),
                &BigInt::from_signed_bytes_be(&self.tag(true)).to_signed_bytes_be()
            ),
            "配对码不一致或握手被篡改"
        );
        let (a, b) = if self.role == "a" {
            (&self.own, &self.peer)
        } else {
            (&self.peer, &self.own)
        };
        self.master = kdf(
            &self.material.to_bytes_be(),
            &Sha256::digest(format!("clipbridge:group-pake:1:{}", self.locator)),
            format!("{a}\n{b}").as_bytes(),
        )?;
        self.stage = 4;
        Ok(())
    }
    pub fn verify_commitment(&self, commitment: &str, nonce: &str) -> Result<()> {
        ensure!(un64(nonce)?.len() == 32, "身份随机数无效");
        let role = if self.role == "a" { "b" } else { "a" };
        let suffix = hash(format!(
            "clipbridge:group-commit:1:{}:{role}:{nonce}:{commitment}",
            self.locator
        ));
        ensure!(
            self.peer.ends_with(&format!(":{}", &suffix[..32])),
            "配对身份承诺不符"
        );
        Ok(())
    }
    pub fn binding(&self) -> String {
        b64(Sha256::digest(&self.master))
    }
    pub fn sas(&self) -> String {
        let h = mac(&self.master, b"clipbridge:group-sas:1");
        format!(
            "{:06}",
            u32::from_be_bytes(h[..4].try_into().unwrap()) % 1_000_000
        )
    }
    pub fn seal<T: Serialize>(&self, slot: &str, value: &T) -> Result<Boxed> {
        ensure!(self.stage == 4, "尚未验证配对");
        Boxed::seal(
            &self.master,
            &format!("clipbridge:group-session:1:{}:{slot}", self.locator),
            value,
        )
    }
    pub fn open<T: serde::de::DeserializeOwned>(&self, slot: &str, value: &Boxed) -> Result<T> {
        ensure!(self.stage == 4, "尚未验证配对");
        value.open(
            &self.master,
            &format!("clipbridge:group-session:1:{}:{slot}", self.locator),
        )
    }
    pub fn open_raw(&self, slot: &str, value: &Boxed) -> Result<String> {
        ensure!(self.stage == 4, "尚未验证配对");
        let cipher = un64(&value.data)?;
        ensure!(cipher.len() <= 200_000, "配对消息过大");
        Ok(String::from_utf8(decrypt(
            &self.master,
            &un64(&value.nonce)?,
            format!("clipbridge:group-session:1:{}:{slot}", self.locator).as_bytes(),
            &cipher,
        )?)?)
    }
    pub fn identity_proof(
        &self,
        own: &Identity,
        peer: &Member,
        a: &str,
        b: &str,
        who: &str,
    ) -> Result<String> {
        let key = kdf(
            &own.agree(peer)?,
            &self.master,
            format!("clipbridge:group-possession:1:{}", self.locator).as_bytes(),
        )?;
        Ok(b64(mac(&key, format!("{who}\n{a}\n{b}").as_bytes())))
    }
}
impl Drop for Pake {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.master.zeroize();
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mutually_authenticated_pake() {
        let code = new_code();
        let mut a = Pake::new(&code, true, "a identity").unwrap();
        let mut b = Pake::new(&code, false, "b identity").unwrap();
        let a1 = a.round1().unwrap();
        let b1 = b.round1().unwrap();
        let a2 = a.round2(&b1).unwrap();
        let b2 = b.round2(&a1).unwrap();
        let a3 = a.round3(&b2).unwrap();
        let b3 = b.round3(&a2).unwrap();
        a.finish(&b3).unwrap();
        b.finish(&a3).unwrap();
        assert_eq!(a.master, b.master);
        assert_eq!(a.sas(), b.sas());
        a.verify_commitment("b identity", &b.commit_nonce).unwrap();
        assert!(a.verify_commitment("changed", &b.commit_nonce).is_err());
        let box_ = a.seal("hello", &"秘密").unwrap();
        assert_eq!(b.open::<String>("hello", &box_).unwrap(), "秘密");
        assert!(b.open::<String>("wrong-slot", &box_).is_err())
    }
}
