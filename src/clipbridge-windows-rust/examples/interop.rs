use anyhow::{Result, ensure};
use clipbridge_desktop::{crypto::*, groups::*, pairing::Session, pake::Pake};
use serde_json::{Value, json};
use std::io::{BufRead, Write};
fn output(value: Value) {
    println!("{}", value);
    std::io::stdout().flush().unwrap();
}
fn read(reader: &mut impl BufRead) -> Result<Value> {
    let mut line = String::new();
    reader.read_line(&mut line)?;
    Ok(serde_json::from_str(&line)?)
}
#[tokio::main]
async fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let mut reader = std::io::BufReader::new(std::io::stdin());
    match args.get(1).map(String::as_str) {
        Some("pake") => {
            let code = &args[2];
            let mut p = Pake::new(code, args[3] == "a", "rust identity")?;
            let m = p.round1()?;
            output(json!({"message":m,"nonce":p.commit_nonce}));
            let r = serde_json::from_value(read(&mut reader)?)?;
            output(serde_json::to_value(p.round2(&r)?)?);
            let r = serde_json::from_value(read(&mut reader)?)?;
            output(serde_json::to_value(p.round3(&r)?)?);
            let r = serde_json::from_value(read(&mut reader)?)?;
            p.finish(&r)?;
            output(json!({"binding":p.binding(),"sas":p.sas()}));
        }
        Some("fixtures") => {
            let own = Identity::new("Rust 中文电脑")?;
            output(json!({"member":own.member}));
            let input = read(&mut reader)?;
            let group: Group = serde_json::from_value(input["group"].clone())?;
            group.validate()?;
            let peer: Member = serde_json::from_value(input["peer"].clone())?;
            let config = group.channel(&own, &peer)?;
            let inbound: Envelope = serde_json::from_value(input["envelope"].clone())?;
            let text: Text = inbound.open(&config)?;
            text.validate()?;
            ensure!(text.text == "C# → Rust 中文", "文本互操作失败");
            let cipher = un64(input["chunk"].as_str().unwrap())?;
            ensure!(
                chunk(&config, &inbound.id, 0, &cipher, false)? == b"binary fixture",
                "附件解密失败"
            );
            let payload = Text {
                kind: "text".into(),
                text: "Rust → C# 中文".into(),
                created: now(),
                expires: now() + 600,
                event_id: id(),
                origin: own.member.id.clone(),
                event_clock: 1,
            };
            let envelope = Envelope::seal(&config, 2, &payload)?;
            let bytes = chunk(&config, &envelope.id, 0, b"rust attachment", true)?;
            output(
                json!({"config":config,"envelope":envelope,"chunk":b64(bytes),"signature":own.sign(b"rust-signed")?}),
            );
        }
        Some("session-host") | Some("session-join") => {
            let dir = std::path::PathBuf::from(&args[2]);
            let input: Value = serde_json::from_slice(&std::fs::read(dir.join("input.json"))?)?;
            let own = Identity::new("Rust full session 中文")?;
            let connection: Connection = serde_json::from_value(input["connection"].clone())?;
            let mut session = if args[1] == "session-host" {
                let group = Group::new(&own, "Rust group", connection)?;
                clipbridge_desktop::network::provision(
                    &group,
                    input["setup"].as_str().unwrap_or(""),
                )
                .await?;
                clipbridge_desktop::network::publish(&group, &own).await?;
                let p = Session::invite(group, own.clone()).await?;
                std::fs::write(
                    dir.join("start.json"),
                    serde_json::to_vec(&json!({"code":p.code,"connection":input["connection"]}))?,
                )?;
                p
            } else {
                Session::join(connection, input["code"].as_str().unwrap(), own.clone()).await?
            };
            let deadline = std::time::Instant::now();
            while !session.done {
                session.step().await?;
                if session.ready() && !session.approved {
                    std::fs::write(dir.join("rust-sas.txt"), &session.sas)?;
                    if let Ok(sas) = std::fs::read_to_string(dir.join("peer-sas.txt")) {
                        ensure!(sas == session.sas, "安全码不一致");
                        session.approve()?;
                    }
                }
                ensure!(deadline.elapsed().as_secs() < 120, "完整配对超时");
                tokio::time::sleep(std::time::Duration::from_millis(150)).await;
            }
            let group = session.group.unwrap();
            group.validate()?;
            let peer = session.peer.unwrap();
            std::fs::write(
                dir.join("result.json"),
                serde_json::to_vec(
                    &json!({"member":own.member,"config":group.channel(&own,&peer)?}),
                )?,
            )?;
            println!("PASS full Rust group pairing");
        }
        _ => anyhow::bail!("unknown interop mode"),
    };
    Ok(())
}
