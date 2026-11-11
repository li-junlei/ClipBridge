use crate::{crypto::*, groups::*};
use anyhow::{Result, bail, ensure};
use futures::StreamExt;
use reqwest::{Client, Method, StatusCode};
use serde::{Serialize, de::DeserializeOwned};
use std::{collections::BTreeSet, time::Duration};
static HTTP_CLIENT: std::sync::OnceLock<Client> = std::sync::OnceLock::new();
static DIRECTORY_CACHE: std::sync::LazyLock<std::sync::Mutex<BTreeSet<String>>> =
    std::sync::LazyLock::new(Default::default);
#[derive(Clone)]
pub struct Http {
    client: Client,
    root: String,
    auth: Option<(String, String)>,
}
impl Http {
    pub fn new(root: &str, auth: Option<(String, String)>) -> Result<Self> {
        let client = HTTP_CLIENT
            .get_or_init(|| {
                Client::builder()
                    .redirect(reqwest::redirect::Policy::none())
                    .connect_timeout(Duration::from_secs(10))
                    .timeout(Duration::from_secs(30))
                    .pool_idle_timeout(Duration::from_secs(120))
                    .pool_max_idle_per_host(8)
                    .user_agent("ClipBridge/0.7.0")
                    .build()
                    .expect("TLS client configuration")
            })
            .clone();
        Ok(Self {
            client,
            root: format!("{}/", root.trim_end_matches('/')),
            auth,
        })
    }
    pub fn for_group(g: &Group) -> Result<(Self, String)> {
        if g.connection.transport == "relay" {
            Ok((
                Self::new(
                    &format!(
                        "{}/v2/groups/{}/store/",
                        g.connection.server.trim_end_matches('/'),
                        g.id
                    ),
                    Some(("group".into(), g.access.clone())),
                )?,
                String::new(),
            ))
        } else {
            Ok((
                Self::new(
                    &g.connection.server,
                    Some((g.connection.username.clone(), g.connection.password.clone())),
                )?,
                g.prefix(),
            ))
        }
    }
    fn scope(&self) -> String {
        hash(serde_json::to_vec(&(&self.root, &self.auth)).expect("connection serialization"))
    }
    fn invalidate_directories(&self) {
        let prefix = format!("{}:", self.scope());
        DIRECTORY_CACHE
            .lock()
            .unwrap()
            .retain(|key| !key.starts_with(&prefix));
    }
    fn request(&self, method: Method, path: &str, bearer: Option<&str>) -> reqwest::RequestBuilder {
        let mut r = self
            .client
            .request(method, format!("{}{path}", self.root))
            .header("Cache-Control", "no-cache, no-store");
        if let Some((user, password)) = &self.auth {
            r = r.basic_auth(user, Some(password))
        }
        if let Some(token) = bearer {
            r = r.bearer_auth(token)
        }
        r
    }
    async fn status(response: reqwest::Response) -> Result<reqwest::Response> {
        match response.status() {
            StatusCode::UNAUTHORIZED => bail!("账号、密码或服务器管理码不正确"),
            StatusCode::FORBIDDEN => bail!("服务器拒绝访问，请检查读写权限"),
            StatusCode::TOO_MANY_REQUESTS => bail!("服务商限频，请稍后重试"),
            s if s.is_redirection() => bail!("服务器返回重定向，请填写最终 HTTPS 地址"),
            _ => Ok(response.error_for_status()?),
        }
    }
    pub async fn get_bytes(&self, path: &str, max: usize) -> Result<Option<Vec<u8>>> {
        let response = self.request(Method::GET, path, None).send().await?;
        if response.status() == StatusCode::NOT_FOUND {
            return Ok(None);
        }
        let response = Self::status(response).await?;
        if let Some(len) = response.content_length() {
            ensure!(len <= max as u64, "服务器响应过大")
        }
        let mut result = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk?;
            ensure!(result.len() + chunk.len() <= max, "服务器响应过大");
            result.extend_from_slice(&chunk)
        }
        Ok(Some(result))
    }
    pub async fn get<T: DeserializeOwned>(&self, path: &str) -> Result<Option<T>> {
        self.get_bytes(path, 250_000)
            .await?
            .map(|b| serde_json::from_slice(&b).map_err(Into::into))
            .transpose()
    }
    pub async fn response(&self, path: &str) -> Result<reqwest::Response> {
        Self::status(
            self.request(Method::GET, path, None)
                .timeout(Duration::from_secs(600))
                .send()
                .await?,
        )
        .await
    }
    pub async fn put_stream(&self, path: &str, body: reqwest::Body, length: u64) -> Result<()> {
        Self::status(
            self.request(Method::PUT, path, None)
                .timeout(Duration::from_secs(600))
                .header("Content-Length", length)
                .header("Content-Type", "application/octet-stream")
                .body(body)
                .send()
                .await?,
        )
        .await?;
        Ok(())
    }
    pub async fn put_bytes(&self, path: &str, body: Vec<u8>) -> Result<()> {
        Self::status(
            self.request(Method::PUT, path, None)
                .timeout(Duration::from_secs(600))
                .header("Content-Type", "application/octet-stream")
                .body(body)
                .send()
                .await?,
        )
        .await?;
        Ok(())
    }
    pub async fn put<T: Serialize>(
        &self,
        path: &str,
        data: &T,
        bearer: Option<&str>,
        create: bool,
    ) -> Result<()> {
        let mut r = self.request(Method::PUT, path, bearer).json(data);
        if create {
            r = r.header("If-None-Match", "*")
        }
        let response = r.send().await?;
        if matches!(
            response.status(),
            StatusCode::NOT_FOUND | StatusCode::CONFLICT
        ) {
            self.invalidate_directories();
        }
        Self::status(response).await?;
        Ok(())
    }
    pub async fn post<T: Serialize>(
        &self,
        path: &str,
        data: &T,
        bearer: Option<&str>,
    ) -> Result<()> {
        Self::status(
            self.request(Method::POST, path, bearer)
                .json(data)
                .send()
                .await?,
        )
        .await?;
        Ok(())
    }
    pub async fn delete(&self, path: &str) -> Result<()> {
        let r = self.request(Method::DELETE, path, None).send().await?;
        if matches!(
            r.status(),
            StatusCode::NOT_FOUND
                | StatusCode::FORBIDDEN
                | StatusCode::METHOD_NOT_ALLOWED
                | StatusCode::NOT_IMPLEMENTED
        ) {
            return Ok(());
        }
        Self::status(r).await?;
        Ok(())
    }
    pub async fn ensure(&mut self, path: &str) -> Result<()> {
        let mut parent = String::new();
        for part in path.split('/').filter(|p| !p.is_empty()) {
            ensure!(!matches!(part, "." | ".."), "路径无效");
            parent.push_str(part);
            parent.push('/');
            let cache_key = format!("{}:{parent}", self.scope());
            if DIRECTORY_CACHE.lock().unwrap().contains(&cache_key) {
                continue;
            }
            let r = self
                .request(Method::from_bytes(b"MKCOL")?, &parent, None)
                .send()
                .await?;
            if !r.status().is_success() && r.status() != StatusCode::METHOD_NOT_ALLOWED {
                let probe = self
                    .request(Method::from_bytes(b"PROPFIND")?, &parent, None)
                    .header("Depth", "0")
                    .send()
                    .await?;
                if probe.status() != StatusCode::MULTI_STATUS {
                    Self::status(r).await?;
                }
            }
            let mut cache = DIRECTORY_CACHE.lock().unwrap();
            if cache.len() > 4096 {
                cache.clear();
            }
            cache.insert(cache_key);
        }
        Ok(())
    }
}
pub async fn provision(g: &Group, setup: &str) -> Result<()> {
    g.validate()?;
    if g.connection.transport == "relay" {
        let http = Http::new(&g.connection.server, None)?;
        http.post("v2/groups",&serde_json::json!({"id":g.id,"authHash":b64(sha2::Sha256::digest(g.access.as_bytes()))}),Some(setup)).await?
    }
    Ok(())
}
pub async fn publish(g: &Group, own: &Identity) -> Result<()> {
    let (mut http, prefix) = Http::for_group(g)?;
    http.ensure(&format!("{prefix}members/")).await?;
    http.put(
        &format!("{prefix}members/{}.json", own.member.id),
        &Boxed::seal(
            &un64(&g.directory_key)?,
            &format!("clipbridge:group-directory:1:{}:{}", g.id, own.member.id),
            &g.feed(own)?,
        )?,
        None,
        false,
    )
    .await
}
pub async fn read_feed(g: &Group, id: &str) -> Result<Option<Feed>> {
    valid_id(id)?;
    let (http, prefix) = Http::for_group(g)?;
    http.get::<Boxed>(&format!("{prefix}members/{id}.json"))
        .await?
        .map(|b| {
            b.open(
                &un64(&g.directory_key)?,
                &format!("clipbridge:group-directory:1:{}:{id}", g.id),
            )
        })
        .transpose()
}
pub async fn refresh(g: &mut Group, own: &Identity) -> Result<()> {
    g.validate()?;
    let mut changed = true;
    let mut read = BTreeSet::new();
    for _ in 0..8 {
        if !changed {
            break;
        }
        changed = false;
        for id in g.members.keys().cloned().collect::<Vec<_>>() {
            if id == own.member.id || g.removed.contains(&id) || read.contains(&id) {
                continue;
            }
            if let Some(feed) = read_feed(g, &id).await? {
                changed |= g.accept(feed)?
            }
            read.insert(id);
        }
    }
    g.status = "连接正常".into();
    Ok(())
}
pub fn channel_http(c: &Config) -> Result<Http> {
    Http::new(&c.server, Some((c.username.clone(), c.password.clone())))
}
pub fn clip_path(c: &Config, own: bool, suffix: &str) -> String {
    format!(
        "clipbridge/{}/{}.{}.json",
        c.pair,
        if own { &c.device } else { &c.peer },
        suffix
    )
}
pub async fn put_clip(http: &mut Http, c: &Config, suffix: &str, e: &Envelope) -> Result<()> {
    http.ensure(&format!("clipbridge/{}/", c.pair)).await?;
    http.put(&clip_path(c, true, suffix), e, None, false).await
}
use sha2::Digest;
