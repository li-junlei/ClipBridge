use crate::crypto::*;
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
#[derive(Clone, Serialize, Deserialize, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct Connection {
    pub transport: String,
    pub server: String,
    #[serde(default)]
    pub username: String,
    #[serde(default)]
    pub password: String,
}
impl Connection {
    pub fn validate(&self) -> Result<()> {
        ensure!(
            self.transport == "relay" || self.transport == "webdav",
            "连接类型无效"
        );
        let u = reqwest::Url::parse(&self.server)?;
        ensure!(
            u.scheme() == "https"
                || (u.scheme() == "http"
                    && matches!(u.host_str(), Some("localhost" | "127.0.0.1" | "[::1]"))),
            "请使用 HTTPS 地址"
        );
        ensure!(
            u.username().is_empty()
                && u.password().is_none()
                && u.query().is_none()
                && u.fragment().is_none(),
            "地址不能包含凭据、查询或片段"
        );
        if self.transport == "relay" {
            ensure!(u.path() == "/", "中转地址需为域名和端口")
        } else {
            ensure!(
                !self.username.is_empty()
                    && self.username.len() <= 256
                    && !self.username.contains(':')
                    && !self.username.chars().any(char::is_control)
                    && !self.password.is_empty()
                    && self.password.len() <= 1024
                    && !self.password.chars().any(char::is_control),
                "WebDAV 账号或密码无效"
            )
        }
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Certificate {
    pub group: String,
    pub issuer: String,
    pub member: Member,
    pub signature: String,
    #[serde(default)]
    pub admission: String,
    #[serde(default)]
    pub previous: String,
}
impl Certificate {
    pub fn data(&self) -> Vec<u8> {
        let mut s = format!(
            "clipbridge:group-member:1\n{}\n{}\n{}\n{}\n{}\n{}",
            self.group,
            self.issuer,
            self.member.id,
            b64(&self.member.name),
            self.member.signing_key,
            self.member.agreement_key
        );
        if !self.admission.is_empty() {
            s += &format!(
                "\nclipbridge:readmission:1\n{}\n{}",
                self.admission, self.previous
            )
        }
        s.into_bytes()
    }
    pub fn hash(&self) -> String {
        hash(format!("{}\n{}", b64(self.data()), self.signature))
    }
    pub fn revoke(&self) -> String {
        if self.admission.is_empty() {
            self.member.id.clone()
        } else {
            format!("{}:{}", self.member.id, self.admission)
        }
    }
    pub fn validate_epoch(&self) -> Result<()> {
        if self.admission.is_empty() {
            ensure!(self.previous.is_empty(), "授权批次无效")
        } else {
            valid_id(&self.admission)?;
            ensure!(
                self.issuer != self.member.id
                    && self.previous.len() == 64
                    && self
                        .previous
                        .bytes()
                        .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c)),
                "重新加入授权无效"
            )
        }
        Ok(())
    }
    pub fn new(
        group: &str,
        issuer: &Identity,
        member: &Member,
        previous: Option<&Certificate>,
    ) -> Result<Self> {
        member.validate()?;
        let mut c = Self {
            group: group.into(),
            issuer: issuer.member.id.clone(),
            member: member.clone(),
            signature: String::new(),
            admission: previous.map(|_| id()).unwrap_or_default(),
            previous: previous.map(Self::hash).unwrap_or_default(),
        };
        c.signature = issuer.sign(&c.data())?;
        Ok(c)
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Feed {
    pub group: String,
    pub author: String,
    pub sequence: i64,
    pub certificates: Vec<Certificate>,
    pub removed: Vec<String>,
    pub signature: String,
}
impl Feed {
    pub fn data(&self) -> Vec<u8> {
        let mut certs: Vec<_> = self.certificates.iter().map(Certificate::hash).collect();
        certs.sort();
        let mut removed = self.removed.clone();
        removed.sort();
        format!(
            "clipbridge:group-feed:1\n{}\n{}\n{}\n{}\n{}",
            self.group,
            self.author,
            self.sequence,
            certs.join("\n"),
            removed.join(",")
        )
        .into_bytes()
    }
}
fn yes() -> bool {
    true
}
fn one() -> i64 {
    1
}
fn storage() -> u32 {
    1
}
#[derive(Clone, Serialize, Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct Group {
    pub id: String,
    pub name: String,
    #[serde(default = "storage")]
    pub storage_version: u32,
    pub connection: Connection,
    pub access: String,
    pub directory_key: String,
    pub root: Certificate,
    pub members: BTreeMap<String, Certificate>,
    #[serde(default)]
    pub feeds: BTreeMap<String, Feed>,
    #[serde(default)]
    pub removed: BTreeSet<String>,
    #[serde(default)]
    pub revoked: BTreeSet<String>,
    #[serde(default)]
    pub own_certificates: Vec<Certificate>,
    #[serde(default)]
    pub own_removed: Vec<String>,
    #[serde(default = "one")]
    pub sequence: i64,
    #[serde(default = "yes")]
    pub enabled: bool,
    #[serde(default)]
    pub suppressed_peers: BTreeSet<String>,
    #[serde(default)]
    pub last_sync: i64,
    #[serde(default)]
    pub status: String,
}
impl Group {
    pub fn new(own: &Identity, name: &str, connection: Connection) -> Result<Self> {
        connection.validate()?;
        ensure!(
            !name.trim().is_empty()
                && name.chars().count() <= 64
                && !name.chars().any(char::is_control),
            "同步组名称无效"
        );
        let id = id();
        let root = Certificate::new(&id, own, &own.member, None)?;
        Ok(Self {
            id,
            name: name.into(),
            storage_version: 2,
            connection,
            access: b64(random(32)),
            directory_key: b64(random(32)),
            root: root.clone(),
            members: BTreeMap::from([(own.member.id.clone(), root)]),
            feeds: BTreeMap::new(),
            removed: BTreeSet::new(),
            revoked: BTreeSet::new(),
            own_certificates: vec![],
            own_removed: vec![],
            sequence: 1,
            enabled: true,
            suppressed_peers: BTreeSet::new(),
            last_sync: 0,
            status: "等待连接".into(),
        })
    }
    pub fn refresh_removed(&mut self) {
        for f in self.feeds.values() {
            self.revoked.extend(f.removed.iter().cloned())
        }
        if self.revoked.is_empty() {
            self.revoked.extend(self.removed.iter().cloned())
        }
        self.removed = self
            .members
            .values()
            .filter(|c| self.revoked.contains(&c.revoke()))
            .map(|c| c.member.id.clone())
            .collect()
    }
    pub fn validate(&self) -> Result<()> {
        valid_id(&self.id)?;
        self.connection.validate()?;
        ensure!(
            un64(&self.directory_key)?.len() == 32
                && (1..=32).contains(&self.members.len())
                && !self.name.is_empty()
                && self.name.chars().count() <= 64,
            "同步组数据无效"
        );
        ensure!(
            self.root.group == self.id && self.root.issuer == self.root.member.id,
            "信任锚无效"
        );
        self.root
            .member
            .verify(&self.root.data(), &self.root.signature)?;
        let mut valid = BTreeSet::from([self.root.member.id.clone()]);
        for _ in 0..32 {
            for (id, c) in &self.members {
                valid_id(id)?;
                ensure!(*id == c.member.id && c.group == self.id, "成员标识无效");
                c.member.validate()?;
                c.validate_epoch()?;
                if valid.contains(&c.issuer) {
                    let issuer = if c.issuer == self.root.member.id {
                        &self.root
                    } else {
                        self.members
                            .get(&c.issuer)
                            .ok_or_else(|| anyhow::anyhow!("授权链缺失"))?
                    };
                    issuer.member.verify(&c.data(), &c.signature)?;
                    valid.insert(c.member.id.clone());
                }
            }
        }
        ensure!(
            valid.len() == self.members.len()
                && self.members.get(&self.root.member.id).map(|c| &c.member)
                    == Some(&self.root.member),
            "授权链不完整"
        );
        for c in self.members.values() {
            self.members
                .get(&c.issuer)
                .ok_or_else(|| anyhow::anyhow!("签发者缺失"))?
                .member
                .verify(&c.data(), &c.signature)?
        }
        if self.connection.transport == "relay" {
            ensure!(un64(&self.access)?.len() == 32, "存储认证无效")
        }
        for f in self.feeds.values() {
            ensure!(f.group == self.id, "成员清单跨组");
            self.members
                .get(&f.author)
                .ok_or_else(|| anyhow::anyhow!("清单签发者缺失"))?
                .member
                .verify(&f.data(), &f.signature)?
        }
        Ok(())
    }
    pub fn feed(&self, own: &Identity) -> Result<Feed> {
        let mut f = Feed {
            group: self.id.clone(),
            author: own.member.id.clone(),
            sequence: self.sequence,
            certificates: self.own_certificates.clone(),
            removed: self.own_removed.clone(),
            signature: String::new(),
        };
        f.signature = own.sign(&f.data())?;
        Ok(f)
    }
    pub fn accept(&mut self, f: Feed) -> Result<bool> {
        self.refresh_removed();
        valid_id(&f.author)?;
        ensure!(
            f.group == self.id
                && f.sequence > 0
                && f.certificates.len() <= 128
                && f.removed.len() <= 128,
            "成员清单无效"
        );
        if self.removed.contains(&f.author) || !self.members.contains_key(&f.author) {
            return Ok(false);
        }
        let author = &self.members[&f.author].member;
        author.verify(&f.data(), &f.signature)?;
        let mut newer = true;
        if let Some(old) = self.feeds.get(&f.author) {
            if f.sequence < old.sequence {
                return Ok(false);
            }
            if f.sequence == old.sequence {
                ensure!(f.data() == old.data(), "成员清单发生分叉");
                newer = false
            }
        }
        for c in &f.certificates {
            c.member.validate()?;
            c.validate_epoch()?;
            ensure!(c.group == self.id && c.issuer == f.author, "授权来源无效");
            author.verify(&c.data(), &c.signature)?;
            if let Some(old) = self.members.get(&c.member.id) {
                ensure!(old.member == c.member, "设备密钥变化")
            }
        }
        for token in &f.removed {
            let parts: Vec<_> = token.split(':').collect();
            ensure!(parts.len() <= 2, "撤销记录无效");
            for p in parts {
                valid_id(p)?
            }
        }
        let mut members = self.members.clone();
        let mut changed = false;
        for c in &f.certificates {
            let replace = members
                .get(&c.member.id)
                .map(|old| !c.admission.is_empty() && c.previous == old.hash())
                .unwrap_or(true);
            if replace {
                members.insert(c.member.id.clone(), c.clone());
                changed = true
            }
        }
        let revoked: BTreeSet<_> = self
            .revoked
            .union(&f.removed.iter().cloned().collect())
            .cloned()
            .collect();
        ensure!(
            members.len() <= 32
                && members
                    .values()
                    .filter(|c| !revoked.contains(&c.revoke()))
                    .count()
                    <= 8,
            "同步组人数达到上限"
        );
        self.members = members;
        self.revoked = revoked;
        self.feeds.insert(f.author.clone(), f);
        self.refresh_removed();
        Ok(newer || changed)
    }
    pub fn add(&mut self, own: &Identity, member: &Member) -> Result<()> {
        self.refresh_removed();
        ensure!(!self.removed.contains(&own.member.id), "邀请方已退出");
        let previous = self.members.get(&member.id);
        if let Some(old) = previous {
            ensure!(
                self.removed.contains(&member.id) && old.member == *member,
                "设备已加入或密钥不符"
            )
        }
        ensure!(
            self.members.len() < 32 || previous.is_some(),
            "历史成员达到上限"
        );
        ensure!(
            self.members
                .keys()
                .filter(|id| !self.removed.contains(*id))
                .count()
                < 8
                && self.own_certificates.len() < 128,
            "同步组已满"
        );
        self.own_certificates
            .push(Certificate::new(&self.id, own, member, previous)?);
        self.sequence += 1;
        self.accept(self.feed(own)?)?;
        Ok(())
    }
    pub fn remove(&mut self, own: &Identity, member: &str) -> Result<()> {
        ensure!(!self.removed.contains(&own.member.id), "本机已退出");
        let c = self
            .members
            .get(member)
            .ok_or_else(|| anyhow::anyhow!("设备不存在"))?;
        let token = c.revoke();
        if !self.own_removed.contains(&token) {
            self.own_removed.push(token)
        }
        self.sequence += 1;
        self.accept(self.feed(own)?)?;
        if member == own.member.id {
            self.enabled = false
        }
        Ok(())
    }
    pub fn export(&self) -> Self {
        let mut g = self.clone();
        g.own_certificates.clear();
        g.own_removed.clear();
        g.sequence = 1;
        g.suppressed_peers.clear();
        g
    }
    pub fn install(mut self, old: Option<&Group>, own: &Identity) -> Result<Self> {
        self.validate()?;
        self.refresh_removed();
        let cert = self
            .members
            .get(&own.member.id)
            .ok_or_else(|| anyhow::anyhow!("缺少本机授权"))?;
        ensure!(
            cert.member == own.member && !self.removed.contains(&own.member.id),
            "本机授权无效"
        );
        if let Some(old) = old {
            ensure!(
                old.root.hash() == self.root.hash() && old.directory_key == self.directory_key,
                "同步组身份变化"
            );
            ensure!(
                !cert.admission.is_empty()
                    && cert.hash() != old.members[&own.member.id].hash()
                    && (old.removed.contains(&own.member.id)
                        || self.revoked.contains(&old.members[&own.member.id].revoke())),
                "本机已在同步组内"
            );
            self.revoked.extend(old.revoked.clone())
        }
        let issuer = cert.issuer.clone();
        let prior = self.feeds.get(&own.member.id);
        self.own_certificates = old.map(|g| g.own_certificates.clone()).unwrap_or_default();
        self.own_removed = old.map(|g| g.own_removed.clone()).unwrap_or_default();
        if let Some(f) = prior {
            for c in &f.certificates {
                if !self
                    .own_certificates
                    .iter()
                    .any(|old| old.hash() == c.hash())
                {
                    self.own_certificates.push(c.clone())
                }
            }
            for t in &f.removed {
                if !self.own_removed.contains(t) {
                    self.own_removed.push(t.clone())
                }
            }
        }
        self.sequence = old
            .map(|g| g.sequence)
            .unwrap_or(0)
            .max(prior.map(|f| f.sequence).unwrap_or(0))
            + 1;
        self.enabled = true;
        self.suppressed_peers.clear();
        self.feeds.insert(own.member.id.clone(), self.feed(own)?);
        self.refresh_removed();
        ensure!(
            !self.removed.contains(&own.member.id) && !self.removed.contains(&issuer),
            "当前邀请授权已被撤销"
        );
        Ok(self)
    }
    pub fn prefix(&self) -> String {
        format!(
            "{}{}/",
            if self.storage_version >= 2 {
                "clipbridge/groups/"
            } else {
                "clipbridge-groups/"
            },
            self.id
        )
    }
    pub fn pair_id(&self, own: &Member, peer: &Member) -> String {
        let mut ids = [own.id.as_str(), peer.id.as_str()];
        ids.sort();
        hash(format!(
            "clipbridge:mesh-pair:1:{}:{}:{}",
            self.id, ids[0], ids[1]
        ))[..32]
            .to_owned()
    }
    pub fn channel(&self, own: &Identity, peer: &Member) -> Result<Config> {
        let pair = self.pair_id(&own.member, peer);
        let key = kdf(
            &own.agree(peer)?,
            &sha2::Sha256::digest(format!("clipbridge:group-channel:1:{}", self.id)),
            pair.as_bytes(),
        )?;
        let device = hash(format!("{pair}:{}", own.member.id))[..32].to_owned();
        let to = hash(format!("{pair}:{}", peer.id))[..32].to_owned();
        let relay = self.connection.transport == "relay";
        Ok(Config {
            server: if relay {
                format!(
                    "{}/v2/groups/{}/store/",
                    self.connection.server.trim_end_matches('/'),
                    self.id
                )
            } else {
                format!(
                    "{}/{}",
                    self.connection.server.trim_end_matches('/'),
                    self.prefix()
                )
            },
            pair: pair.clone(),
            device: device.clone(),
            peer: to,
            token: b64(sha2::Sha256::digest(format!("{pair}{device}"))),
            key: b64(key),
            transport: "webdav".into(),
            username: if relay {
                "group".into()
            } else {
                self.connection.username.clone()
            },
            password: if relay {
                self.access.clone()
            } else {
                self.connection.password.clone()
            },
        })
    }
}
use sha2::Digest;
