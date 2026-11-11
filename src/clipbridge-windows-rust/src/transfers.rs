use crate::{crypto::*, network::*};
use anyhow::{Result, ensure};
use futures::TryStreamExt;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    io::ErrorKind,
    path::{Path, PathBuf},
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct Offer {
    pub id: String,
    pub name: String,
    pub mime: String,
    pub kind: String,
    pub size: u64,
    pub hash: String,
    pub chunks: usize,
    pub expires: i64,
    pub status: String,
    pub sender: String,
}
impl Offer {
    pub fn validate(&self) -> Result<()> {
        valid_id(&self.id)?;
        valid_id(&self.sender)?;
        ensure!(
            matches!(self.kind.as_str(), "file" | "image")
                && matches!(self.status.as_str(), "offered" | "ready" | "cancelled")
                && self.size <= MAX_FILE
                && self.chunks == ((self.size as usize + CHUNK - 1) / CHUNK).max(1)
                && self.hash.len() == 64
                && self
                    .hash
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
                && !self.name.trim().is_empty()
                && self.name.chars().count() <= 180
                && !self.name.chars().any(char::is_control)
                && !self.name.contains(['/', '\\', ':'])
                && !matches!(self.name.as_str(), "." | "..")
                && self.mime.len() <= 100,
            "附件描述无效"
        );
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct Decision {
    pub id: String,
    pub status: String,
}
#[derive(Clone, Serialize, Deserialize, Default)]
pub struct Index {
    pub offers: Vec<Offer>,
    pub decisions: Vec<Decision>,
    pub created: i64,
    pub expires: i64,
}
#[derive(Clone, Serialize, Deserialize, Debug)]
pub struct Job {
    pub offer: Offer,
    pub path: PathBuf,
    pub peer: String,
    pub pair: String,
    pub phase: String,
    #[serde(default)]
    pub uploaded: bool,
    #[serde(default)]
    pub destination: PathBuf,
    #[serde(default)]
    pub token: String,
}
pub fn index_config(c: &Config) -> Config {
    Config {
        key: b64(mac(
            &un64(&c.key).unwrap_or_default(),
            b"clipbridge:attachments-index:1",
        )),
        ..c.clone()
    }
}
pub fn path(c: &Config, own: bool, id: &str) -> String {
    format!(
        "clipbridge/{}/attachments/{}/{id}.cbx",
        c.pair,
        if own { &c.device } else { &c.peer }
    )
}
pub async fn get_index(http: &Http, c: &Config) -> Result<Option<Index>> {
    let Some(e) = http
        .get::<Envelope>(&clip_path(c, false, "transfers"))
        .await?
    else {
        return Ok(None);
    };
    let index: Index = e.open(&index_config(c))?;
    validate_index(index)
}
fn validate_index(index: Index) -> Result<Option<Index>> {
    ensure!(
        index.created <= now() + 60
            && index.expires >= index.created
            && index.expires - index.created <= 3600
            && index.offers.len() <= 32
            && index.decisions.len() <= 128,
        "附件清单过期或无效"
    );
    for o in &index.offers {
        o.validate()?
    }
    for d in &index.decisions {
        valid_id(&d.id)?;
        ensure!(
            matches!(d.status.as_str(), "accepted" | "rejected" | "done"),
            "附件决定无效"
        )
    }
    Ok((index.expires > now()).then_some(index))
}
pub async fn put_index(
    http: &mut Http,
    c: &Config,
    offers: Vec<Offer>,
    decisions: Vec<Decision>,
) -> Result<()> {
    let index = Index {
        offers,
        decisions,
        created: now(),
        expires: now() + 3600,
    };
    let e = Envelope::seal(&index_config(c), millis(), &index)?;
    put_clip(http, c, "transfers", &e).await
}
pub async fn stage(
    source: &Path,
    cache: &Path,
    sender: &str,
    kind: &str,
    peer: &str,
    pair: &str,
) -> Result<Job> {
    ensure!(
        tokio::fs::metadata(source).await?.len() <= MAX_FILE,
        "单文件上限 100 MB，请使用其他大文件传输方式"
    );
    let id = id();
    let destination = cache.join(format!("{id}.send"));
    let result: Result<Job> = async {
        let mut input = tokio::fs::File::open(source).await?;
        let mut out = tokio::fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&destination)
            .await?;
        let mut h = Sha256::new();
        let mut size = 0u64;
        let mut buffer = vec![0; CHUNK];
        loop {
            let n = input.read(&mut buffer).await?;
            if n == 0 {
                break;
            }
            size += n as u64;
            ensure!(size <= MAX_FILE, "文件超过 100 MB");
            h.update(&buffer[..n]);
            out.write_all(&buffer[..n]).await?
        }
        out.sync_all().await?;
        let offer = Offer {
            id: id.clone(),
            name: if kind == "image" {
                "Clipboard.png".into()
            } else {
                safe_name(&source.file_name().unwrap_or_default().to_string_lossy())
            },
            mime: if kind == "image" {
                "image/png"
            } else {
                "application/octet-stream"
            }
            .into(),
            kind: kind.into(),
            size,
            hash: hex::encode(h.finalize()),
            chunks: ((size as usize + CHUNK - 1) / CHUNK).max(1),
            expires: now() + 3600,
            status: "offered".into(),
            sender: sender.into(),
        };
        offer.validate()?;
        Ok(Job {
            offer,
            path: destination.clone(),
            peer: peer.into(),
            pair: pair.into(),
            phase: "waiting".into(),
            uploaded: false,
            destination: PathBuf::new(),
            token: crate::crypto::id(),
        })
    }
    .await;
    if result.is_err() {
        let _ = tokio::fs::remove_file(destination).await;
    }
    result
}
pub async fn upload(http: &mut Http, c: &Config, job: &Job) -> Result<()> {
    http.ensure(&format!("clipbridge/{}/attachments/{}/", c.pair, c.device))
        .await?;
    let file = tokio::fs::File::open(&job.path).await?;
    ensure!(
        file.metadata().await?.len() == job.offer.size,
        "暂存文件已变化"
    );
    let cfg = c.clone();
    let offer = job.offer.clone();
    let stream = futures::stream::try_unfold((file, 0usize), move |(mut file, part)| {
        let cfg = cfg.clone();
        let offer = offer.clone();
        async move {
            if part >= offer.chunks {
                return Ok::<_, std::io::Error>(None);
            }
            let size = (offer.size.saturating_sub((part * CHUNK) as u64) as usize).min(CHUNK);
            let mut plain = vec![0; size];
            file.read_exact(&mut plain).await?;
            let enc = chunk(&cfg, &offer.id, part, &plain, true).map_err(std::io::Error::other)?;
            Ok(Some((enc, (file, part + 1))))
        }
    });
    http.put_stream(
        &path(c, true, &job.offer.id),
        reqwest::Body::wrap_stream(stream),
        job.offer.size + (job.offer.chunks * 28) as u64,
    )
    .await
}
pub async fn download(http: &Http, c: &Config, offer: &Offer, cache: &Path) -> Result<PathBuf> {
    offer.validate()?;
    let destination = cache.join(format!("{}-{}.receive", offer.sender, offer.id));
    let result: Result<()> = async {
        let expected = offer.size + (offer.chunks * 28) as u64;
        let response = http.response(&path(c, false, &offer.id)).await?;
        if let Some(len) = response.content_length() {
            ensure!(len == expected, "附件长度不符")
        }
        let stream = response.bytes_stream().map_err(std::io::Error::other);
        let mut reader = tokio_util::io::StreamReader::new(stream);
        let mut out = tokio::fs::File::create(&destination).await?;
        let mut hash = Sha256::new();
        for part in 0..offer.chunks {
            let size = (offer.size.saturating_sub((part * CHUNK) as u64) as usize).min(CHUNK);
            let mut cipher = vec![0; size + 28];
            reader.read_exact(&mut cipher).await?;
            let plain = chunk(c, &offer.id, part, &cipher, false)?;
            hash.update(&plain);
            out.write_all(&plain).await?
        }
        let mut extra = [0];
        ensure!(reader.read(&mut extra).await? == 0, "附件存在多余数据");
        out.sync_all().await?;
        ensure!(hex::encode(hash.finalize()) == offer.hash, "附件摘要不符");
        Ok(())
    }
    .await;
    if let Err(e) = result {
        let _ = tokio::fs::remove_file(&destination).await;
        return Err(e);
    }
    Ok(destination)
}
pub async fn save_download(job: &Job, directory: &Path) -> Result<PathBuf> {
    tokio::fs::create_dir_all(directory).await?;
    let mut path = directory.join(safe_name(&job.offer.name));
    for attempt in 0..100 {
        match tokio::fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(&path)
            .await
        {
            Ok(mut output) => {
                let result: Result<()> = async {
                    let mut source = tokio::fs::File::open(&job.path).await?;
                    tokio::io::copy(&mut source, &mut output).await?;
                    output.sync_all().await?;
                    let _ = tokio::fs::write(
                        format!("{}:Zone.Identifier", path.display()),
                        b"[ZoneTransfer]\r\nZoneId=3\r\n",
                    )
                    .await;
                    Ok(())
                }
                .await;
                if let Err(e) = result {
                    let _ = tokio::fs::remove_file(&path).await;
                    return Err(e);
                }
                return Ok(path);
            }
            Err(e) if e.kind() == ErrorKind::AlreadyExists => {
                path = directory.join(format!(
                    "{}-{}-{attempt}",
                    job.offer.id,
                    safe_name(&job.offer.name)
                ));
            }
            Err(e) => return Err(e.into()),
        }
    }
    anyhow::bail!("无法分配接收文件名")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn expired_indexes_are_absent_but_malformed_lifetimes_fail() {
        assert!(
            validate_index(Index {
                created: now() - 3600,
                expires: now(),
                ..Default::default()
            })
            .unwrap()
            .is_none()
        );
        assert!(
            validate_index(Index {
                created: now(),
                expires: now() + 3600,
                ..Default::default()
            })
            .unwrap()
            .is_some()
        );
        assert!(
            validate_index(Index {
                created: now() - 7200,
                expires: now(),
                ..Default::default()
            })
            .is_err()
        );
        assert!(
            validate_index(Index {
                created: now() + 3600,
                expires: now() + 7200,
                ..Default::default()
            })
            .is_err()
        );
    }
}
