use crate::{
    crypto::*,
    groups::*,
    network::*,
    pake::{self, Message, Pake},
};
use anyhow::{Result, ensure};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
#[derive(Clone, Serialize, Deserialize)]
pub struct InviteMeta {
    pub group: String,
    pub root: String,
    pub expires: i64,
    pub first: Message,
}
#[derive(Clone, Serialize, Deserialize)]
pub struct JoinLink {
    pub connection: Connection,
    pub code: String,
}
pub fn link(connection: &Connection, code: &str) -> Result<String> {
    Ok(format!(
        "clipbridge://group#{}",
        URL_SAFE_NO_PAD.encode(serde_json::to_vec(&JoinLink {
            connection: connection.clone(),
            code: pake::normalize(code)?
        })?)
    ))
}
pub fn parse(text: &str) -> Result<JoinLink> {
    ensure!(text.len() <= 8192, "邀请过长");
    let encoded = text
        .strip_prefix("clipbridge://group#")
        .ok_or_else(|| anyhow::anyhow!("邀请格式无效"))?;
    let mut v: JoinLink = serde_json::from_slice(&URL_SAFE_NO_PAD.decode(encoded)?)?;
    v.connection.validate()?;
    v.code = pake::normalize(&v.code)?;
    Ok(v)
}
#[derive(Clone)]
struct Signal {
    http: Http,
    prefix: String,
    token: String,
}
impl Signal {
    fn new(c: &Connection, code: &str) -> Result<Self> {
        c.validate()?;
        let locator = &code[..6];
        let dav = c.transport == "webdav";
        Ok(Self {
            http: Http::new(
                &if dav {
                    c.server.clone()
                } else {
                    format!(
                        "{}/v2/invitations/{locator}/",
                        c.server.trim_end_matches('/')
                    )
                },
                if dav {
                    Some((c.username.clone(), c.password.clone()))
                } else {
                    None
                },
            )?,
            prefix: if dav {
                format!("clipbridge/invites/{locator}/")
            } else {
                String::new()
            },
            token: b64(random(32)),
        })
    }
    async fn get<T: serde::de::DeserializeOwned>(&self, slot: &str) -> Result<Option<T>> {
        self.http.get(&format!("{}{slot}.json", self.prefix)).await
    }
    async fn write<T: Serialize>(&self, slot: &str, value: &T) -> Result<()> {
        let path = format!("{}{slot}.json", self.prefix);
        let expected = serde_json::to_value(value)?;
        if let Some(old) = self.http.get::<serde_json::Value>(&path).await? {
            ensure!(old == expected, "配对消息冲突，请生成新邀请");
            return Ok(());
        }
        if let Err(e) = self
            .http
            .put(
                &path,
                value,
                if self.prefix.is_empty() {
                    Some(&self.token)
                } else {
                    None
                },
                true,
            )
            .await
        {
            let old = self.http.get::<serde_json::Value>(&path).await?;
            if old.as_ref() != Some(&expected) {
                return Err(e);
            }
        }
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Hello {
    member: Member,
    group: String,
    name: String,
    root: String,
    commit_nonce: String,
    signature: String,
}
impl Hello {
    fn data(&self, locator: &str, binding: &str) -> Vec<u8> {
        format!(
            "clipbridge:group-hello:1\n{binding}\n{locator}\n{}\n{}\n{}\n{}\n{}\n{}\n{}\n{}",
            self.group,
            self.root,
            self.commit_nonce,
            b64(&self.name),
            self.member.id,
            b64(&self.member.name),
            self.member.signing_key,
            self.member.agreement_key
        )
        .into_bytes()
    }
}
#[derive(Clone, Serialize, Deserialize)]
struct Approval {
    accepted: bool,
    proof: String,
}
pub struct Session {
    own: Identity,
    pake: Pake,
    signal: Signal,
    meta: InviteMeta,
    host: bool,
    pub code: String,
    pub link: String,
    pub group: Option<Group>,
    pub peer: Option<Member>,
    pub sas: String,
    pub status: String,
    pub approved: bool,
    pub done: bool,
    stage: u32,
    second: Option<Message>,
    third: Option<Message>,
    finished: bool,
    boxes: BTreeMap<String, Boxed>,
    own_hello: String,
    peer_hello: String,
    approval_sent: bool,
    peer_approved: bool,
    group_added: bool,
    pending_group: Option<Group>,
    installed: bool,
    grant_sent: bool,
}
impl Session {
    fn build(
        own: Identity,
        pake: Pake,
        signal: Signal,
        meta: InviteMeta,
        host: bool,
        code: String,
        connection: Connection,
        group: Option<Group>,
    ) -> Result<Self> {
        Ok(Self {
            own,
            pake,
            signal,
            meta,
            host,
            link: link(&connection, &code)?,
            code,
            group,
            peer: None,
            sas: String::new(),
            status: "等待对方连接".into(),
            approved: false,
            done: false,
            stage: 0,
            second: None,
            third: None,
            finished: false,
            boxes: BTreeMap::new(),
            own_hello: String::new(),
            peer_hello: String::new(),
            approval_sent: false,
            peer_approved: false,
            group_added: false,
            pending_group: None,
            installed: false,
            grant_sent: false,
        })
    }
    pub async fn invite(mut group: Group, own: Identity) -> Result<Self> {
        group.refresh_removed();
        group.validate()?;
        ensure!(
            group.enabled && !group.removed.contains(&own.member.id),
            "同步组已关闭或本机已退出"
        );
        let code = pake::new_code();
        let mut pake = Pake::new(
            &code,
            true,
            &pake::commitment(&own.member, &group.id, &group.root.hash()),
        )?;
        let meta = InviteMeta {
            group: group.id.clone(),
            root: group.root.hash(),
            expires: now() + 300,
            first: pake.round1()?,
        };
        let mut signal = Signal::new(&group.connection, &code)?;
        if group.connection.transport == "relay" {
            Http::new(&group.connection.server,None)?.post(&format!("v2/groups/{}/invitations/{}",group.id,&code[..6]),&serde_json::json!({"meta":meta,"ownerHash":b64(sha2::Sha256::digest(signal.token.as_bytes()))}),Some(&group.access)).await?
        } else {
            ensure!(
                signal.get::<InviteMeta>("meta").await?.is_none(),
                "邀请编号冲突"
            );
            signal.http.ensure(&signal.prefix).await?;
            signal
                .http
                .put(&format!("{}meta.json", signal.prefix), &meta, None, true)
                .await?
        }
        let connection = group.connection.clone();
        Self::build(own, pake, signal, meta, true, code, connection, Some(group))
    }
    pub async fn join(connection: Connection, code: &str, own: Identity) -> Result<Self> {
        let code = pake::normalize(code)?;
        let signal = Signal::new(&connection, &code)?;
        let meta = signal
            .get::<InviteMeta>("meta")
            .await?
            .ok_or_else(|| anyhow::anyhow!("邀请不存在或已过期"))?;
        valid_id(&meta.group)?;
        ensure!(
            meta.root.len() == 64 && meta.expires > now() && meta.expires <= now() + 360,
            "邀请过期或无效"
        );
        let mut pake = Pake::new(
            &code,
            false,
            &pake::commitment(&own.member, &meta.group, &meta.root),
        )?;
        signal.write("b1", &pake.round1()?).await?;
        Self::build(own, pake, signal, meta, false, code, connection, None)
    }
    pub fn needs_install(&self) -> bool {
        !self.host && self.stage == 6 && !self.installed
    }
    pub fn mark_installed(&mut self) {
        self.installed = true;
    }
    pub fn ready(&self) -> bool {
        self.stage >= 4 && !self.done
    }
    pub fn approve(&mut self) -> Result<()> {
        ensure!(self.ready(), "请等待安全码出现");
        self.approved = true;
        self.status = "已确认，等待对方确认".into();
        Ok(())
    }
    async fn send<T: Serialize>(&mut self, slot: &str, data: &T) -> Result<()> {
        if !self.boxes.contains_key(slot) {
            self.boxes.insert(slot.into(), self.pake.seal(slot, data)?);
        }
        self.signal.write(slot, &self.boxes[slot]).await
    }
    pub async fn step(&mut self) -> Result<()> {
        if self.done {
            return Ok(());
        }
        ensure!(now() < self.meta.expires, "邀请已过期，请重新生成");
        let own = if self.host { "a" } else { "b" };
        let other = if self.host { "b" } else { "a" };
        if self.stage == 0 {
            let first = if self.host {
                self.signal.get::<Message>("b1").await?
            } else {
                Some(self.meta.first.clone())
            };
            let Some(first) = first else { return Ok(()) };
            if self.second.is_none() {
                self.second = Some(self.pake.round2(&first)?)
            }
            self.signal
                .write(&format!("{own}2"), self.second.as_ref().unwrap())
                .await?;
            self.stage = 1;
            self.status = "正在验证配对口令".into()
        }
        if self.stage == 1 {
            let Some(second) = self.signal.get::<Message>(&format!("{other}2")).await? else {
                return Ok(());
            };
            if self.third.is_none() {
                self.third = Some(self.pake.round3(&second)?)
            }
            self.signal
                .write(&format!("{own}3"), self.third.as_ref().unwrap())
                .await?;
            self.stage = 2
        }
        if self.stage == 2 {
            let Some(third) = self.signal.get::<Message>(&format!("{other}3")).await? else {
                return Ok(());
            };
            if !self.finished {
                self.pake.finish(&third)?;
                self.finished = true
            }
            if self.own_hello.is_empty() {
                let mut hello = Hello {
                    member: self.own.member.clone(),
                    group: self.meta.group.clone(),
                    name: self
                        .group
                        .as_ref()
                        .map(|g| g.name.clone())
                        .unwrap_or_default(),
                    root: self.meta.root.clone(),
                    commit_nonce: self.pake.commit_nonce.clone(),
                    signature: String::new(),
                };
                hello.signature = self
                    .own
                    .sign(&hello.data(&self.code[..6], &self.pake.binding()))?;
                self.own_hello = serde_json::to_string(&serde_json::to_value(&hello)?)?
            }
            self.send(
                &format!("{own}hello"),
                &serde_json::from_str::<serde_json::Value>(&self.own_hello)?,
            )
            .await?;
            self.stage = 3
        }
        if self.stage == 3 {
            let slot = format!("{other}hello");
            let Some(box_) = self.signal.get::<Boxed>(&slot).await? else {
                return Ok(());
            };
            self.peer_hello = self.pake.open_raw(&slot, &box_)?;
            let hello: Hello = serde_json::from_str(&self.peer_hello)?;
            hello.member.validate()?;
            ensure!(
                hello.group == self.meta.group
                    && hello.root == self.meta.root
                    && hello.member.id != self.own.member.id,
                "设备身份不匹配"
            );
            self.pake.verify_commitment(
                &pake::commitment(&hello.member, &hello.group, &hello.root),
                &hello.commit_nonce,
            )?;
            hello.member.verify(
                &hello.data(&self.code[..6], &self.pake.binding()),
                &hello.signature,
            )?;
            if let Some(g) = &self.group {
                ensure!(
                    !g.members.contains_key(&hello.member.id)
                        || g.removed.contains(&hello.member.id),
                    "此设备已在同步组内"
                )
            }
            self.peer = Some(hello.member);
            self.sas = self.pake.sas();
            self.stage = 4;
            self.status = "核对双方安全码后，分别确认".into()
        }
        if self.stage == 4 {
            let (a, b) = if self.host {
                (self.own_hello.clone(), self.peer_hello.clone())
            } else {
                (self.peer_hello.clone(), self.own_hello.clone())
            };
            let peer = self.peer.clone().unwrap();
            if self.approved && !self.approval_sent {
                let approval = Approval {
                    accepted: true,
                    proof: self.pake.identity_proof(&self.own, &peer, &a, &b, own)?,
                };
                self.send(&format!("{own}approval"), &approval).await?;
                self.approval_sent = true
            }
            if !self.peer_approved {
                let slot = format!("{other}approval");
                if let Some(box_) = self.signal.get::<Boxed>(&slot).await? {
                    let approval: Approval = self.pake.open(&slot, &box_)?;
                    ensure!(
                        approval.accepted
                            && equal(
                                &un64(&approval.proof)?,
                                &un64(&self.pake.identity_proof(&self.own, &peer, &a, &b, other)?)?
                            ),
                        "设备密钥确认失败"
                    );
                    self.peer_approved = true
                }
            }
            if !self.approved || !self.peer_approved {
                return Ok(());
            }
            if self.host {
                if !self.group_added {
                    let mut pending = self.group.as_ref().unwrap().clone();
                    pending.add(&self.own, &peer)?;
                    self.pending_group = Some(pending);
                    self.group_added = true;
                    return Ok(());
                }
                if !self.grant_sent {
                    self.send("grant", &self.pending_group.as_ref().unwrap().export())
                        .await?;
                    self.grant_sent = true
                }
                self.stage = 5;
                self.status = "等待新设备完成入组".into()
            } else {
                let Some(box_) = self.signal.get::<Boxed>("grant").await? else {
                    return Ok(());
                };
                let mut g: Group = self.pake.open("grant", &box_)?;
                g.refresh_removed();
                g.validate()?;
                ensure!(
                    g.id == self.meta.group
                        && g.root.hash() == self.meta.root
                        && g.members.get(&peer.id).map(|c| &c.member) == Some(&peer)
                        && !g.removed.contains(&peer.id),
                    "入组授权不匹配"
                );
                if let Some(f) = read_feed(&g, &self.own.member.id).await? {
                    ensure!(
                        f.group == g.id && f.author == self.own.member.id,
                        "历史清单不匹配"
                    );
                    self.own.member.verify(&f.data(), &f.signature)?;
                    if f.sequence > g.feeds.get(&f.author).map(|f| f.sequence).unwrap_or(0) {
                        g.feeds.insert(f.author.clone(), f);
                    }
                }
                g.own_certificates.clear();
                g.own_removed.clear();
                g.sequence = 1;
                self.group = Some(g);
                self.stage = 6;
                self.status = "正在保存同步组".into();
                return Ok(());
            }
        }
        if self.stage == 5 {
            if let Some(box_) = self.signal.get::<Boxed>("done").await? {
                let approval: Approval = self.pake.open("done", &box_)?;
                if approval.accepted {
                    self.group = self.pending_group.take();
                    self.done = true;
                    self.status = "新设备已加入".into()
                }
            }
        }
        if self.stage == 6 && self.installed {
            self.send(
                "done",
                &Approval {
                    accepted: true,
                    proof: String::new(),
                },
            )
            .await?;
            self.done = true;
            self.status = "已加入同步组".into();
        }
        Ok(())
    }
    pub fn host(&self) -> bool {
        self.host
    }
}
use sha2::Digest;

#[cfg(test)]
mod tests {
    use super::*;
    #[tokio::test]
    async fn guest_cannot_acknowledge_before_local_install() {
        let own = Identity::new("Guest").unwrap();
        let connection = Connection {
            transport: "relay".into(),
            server: "https://example.invalid".into(),
            username: String::new(),
            password: String::new(),
        };
        let code = pake::new_code();
        let mut pake = Pake::new(&code, false, "test").unwrap();
        let meta = InviteMeta {
            group: id(),
            root: "a".repeat(64),
            expires: now() + 300,
            first: pake.round1().unwrap(),
        };
        let signal = Signal::new(&connection, &code).unwrap();
        let mut session =
            Session::build(own, pake, signal, meta, false, code, connection, None).unwrap();
        session.stage = 6;
        session.approved = true;
        assert!(session.needs_install());
        session.step().await.unwrap();
        assert!(!session.done);
        assert!(!session.boxes.contains_key("done"));
        session.mark_installed();
        assert!(!session.needs_install());
    }
}
