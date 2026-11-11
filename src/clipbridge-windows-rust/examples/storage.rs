use anyhow::{Result, ensure};
use clipbridge_desktop::{crypto::*, groups::*, network::*, transfers::*};
use serde_json::Value;
use std::{io::Read, path::PathBuf};
#[tokio::main]
async fn main() -> Result<()> {
    let mut input = String::new();
    std::io::stdin().read_to_string(&mut input)?;
    let input: Value = serde_json::from_str(&input)?;
    let connection: Connection = serde_json::from_value(input["connection"].clone())?;
    let directory = PathBuf::from(input["directory"].as_str().unwrap());
    let a = Identity::new("Rust sender")?;
    let b = Identity::new("Rust receiver")?;
    let mut group = Group::new(&a, "Rust storage validation", connection)?;
    provision(&group, input["setup"].as_str().unwrap_or("")).await?;
    group.add(&a, &b.member)?;
    publish(&group, &a).await?;
    let feed = read_feed(&group, &a.member.id).await?.unwrap();
    ensure!(
        feed.data() == group.feed(&a)?.data(),
        "成员目录加密往返失败"
    );
    println!("PASS encrypted signed directory feed round trip");
    let ca = group.channel(&a, &b.member)?;
    let cb = group.channel(&b, &a.member)?;
    let mut ha = channel_http(&ca)?;
    let mut hb = channel_http(&cb)?;
    let body = vec![0x4a; 1024 * 1024 + 37];
    let file = directory.join("中文附件.bin");
    tokio::fs::write(&file, &body).await?;
    let job = stage(
        &file,
        &directory,
        &a.member.id,
        "file",
        &b.member.id,
        &ca.pair,
    )
    .await?;
    put_index(&mut ha, &ca, vec![job.offer.clone()], vec![]).await?;
    let remote = get_index(&hb, &cb).await?.unwrap();
    ensure!(remote.offers[0].status == "offered", "请求状态无效");
    ensure!(
        hb.get_bytes(&path(&cb, false, &job.offer.id), 2_000_000)
            .await?
            .is_none(),
        "同意前已上传文件"
    );
    println!("PASS consent request contains metadata but no uploaded attachment body");
    put_index(
        &mut hb,
        &cb,
        vec![],
        vec![Decision {
            id: job.offer.id.clone(),
            status: "accepted".into(),
        }],
    )
    .await?;
    ensure!(
        get_index(&ha, &ca).await?.unwrap().decisions[0].status == "accepted",
        "接收决定失败"
    );
    upload(&mut ha, &ca, &job).await?;
    let received = download(&hb, &cb, &job.offer, &directory).await?;
    ensure!(
        tokio::fs::read(&received).await? == body,
        "流式附件内容不符"
    );
    println!(
        "PASS encrypted streamed CBX transfer crosses five chunks with exact hash and UTF8 filename"
    );
    let mut saved = job.clone();
    saved.path = received;
    saved.destination = std::path::PathBuf::new();
    let destination = save_download(&saved, &directory.join("received")).await?;
    ensure!(tokio::fs::read(destination).await? == body, "保存内容不符");
    ha.delete(&path(&ca, true, &job.offer.id)).await?;
    ensure!(
        hb.get_bytes(&path(&cb, false, &job.offer.id), 2_000_000)
            .await?
            .is_none(),
        "密文未清理"
    );
    println!("PASS saved file integrity and completed ciphertext cleanup");
    println!("GROUP {}", group.id);
    Ok(())
}
