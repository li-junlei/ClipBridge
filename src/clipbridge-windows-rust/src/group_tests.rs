#[cfg(test)]
mod tests {
    use crate::{crypto::*, groups::*, pake::*};
    fn group() -> (Identity, Identity, Group) {
        let a = Identity::new("A").unwrap();
        let b = Identity::new("B").unwrap();
        let g = Group::new(
            &a,
            "组",
            Connection {
                transport: "webdav".into(),
                server: "https://example.invalid/dav/".into(),
                username: "test".into(),
                password: "test".into(),
            },
        )
        .unwrap();
        (a, b, g)
    }
    #[test]
    fn signed_membership_rejoin_and_revocation() {
        let (a, b, mut g) = group();
        g.add(&a, &b.member).unwrap();
        g.validate().unwrap();
        let key = g.channel(&a, &b.member).unwrap().key;
        let mut guest = g.export().install(None, &b).unwrap();
        g.remove(&a, &b.member.id).unwrap();
        let old = g.feed(&a).unwrap();
        guest.accept(old.clone()).unwrap();
        assert!(guest.removed.contains(&b.member.id));
        g.add(&a, &b.member).unwrap();
        let fresh = g.export().install(Some(&guest), &b).unwrap();
        fresh.validate().unwrap();
        assert!(!fresh.removed.contains(&b.member.id));
        assert_eq!(key, fresh.channel(&b, &a.member).unwrap().key);
        let mut fresh = fresh;
        fresh.accept(old).unwrap();
        assert!(!fresh.removed.contains(&b.member.id));
        g.remove(&a, &b.member.id).unwrap();
        fresh.accept(g.feed(&a).unwrap()).unwrap();
        assert!(fresh.removed.contains(&b.member.id));
    }
    #[test]
    fn invitation_from_revoked_member_cannot_install() {
        let (a, b, mut group) = group();
        group.add(&a, &b.member).unwrap();
        group.remove(&a, &a.member.id).unwrap();
        assert!(group.export().install(None, &b).is_err());
    }
    #[test]
    fn untrusted_author_and_forked_feed_rejected() {
        let (a, b, mut g) = group();
        g.add(&a, &b.member).unwrap();
        let mut fake = g.feed(&a).unwrap();
        fake.author = b.member.id.clone();
        assert!(g.accept(fake).is_err());
        let mut altered = g.clone();
        altered.members.get_mut(&b.member.id).unwrap().member.name = "篡改名称".into();
        assert!(altered.validate().is_err());
        let other = Identity::new("B").unwrap();
        let fake = Member {
            id: b.member.id.clone(),
            ..other.member
        };
        assert!(g.add(&a, &fake).is_err());
        let mut f = g.feed(&a).unwrap();
        f.removed.push(b.member.id.clone());
        f.signature = a.sign(&f.data()).unwrap();
        assert!(g.accept(f).is_err());
    }
    #[test]
    fn protocol_domains_and_paths_are_bound() {
        let (a, b, mut g) = group();
        g.add(&a, &b.member).unwrap();
        let ca = g.channel(&a, &b.member).unwrap();
        let cb = g.channel(&b, &a.member).unwrap();
        assert_eq!(ca.key, cb.key);
        assert_eq!(ca.device, cb.peer);
        let mut g2 = g.clone();
        g2.id = id();
        assert_ne!(g2.channel(&a, &b.member).unwrap().key, ca.key);
        let plaintext = serde_json::json!({"secret":"payload"});
        let envelope =
            Boxed::seal(&un64(&g.directory_key).unwrap(), "group-a", &plaintext).unwrap();
        assert!(
            envelope
                .open::<serde_json::Value>(&un64(&g.directory_key).unwrap(), "group-b")
                .is_err()
        );
        assert_eq!(safe_name("CON.txt"), "_CON.txt");
        assert!(!safe_name("../../bad.exe").contains('/'));
    }
    #[test]
    fn bad_pake_proof_and_wrong_code_fail() {
        let code = new_code();
        let mut wrong = code.clone().into_bytes();
        wrong[15] = if wrong[15] == b'2' { b'3' } else { b'2' };
        let wrong = String::from_utf8(wrong).unwrap();
        let mut a = Pake::new(&code, true, "").unwrap();
        let mut b = Pake::new(&wrong, false, "").unwrap();
        let a1 = a.round1().unwrap();
        let mut b1 = b.round1().unwrap();
        let a2 = a.round2(&b1).unwrap();
        let b2 = b.round2(&a1).unwrap();
        let a3 = a.round3(&b2).unwrap();
        let b3 = b.round3(&a2).unwrap();
        assert!(a.finish(&b3).is_err());
        assert!(b.finish(&a3).is_err());
        let mut fresh = Pake::new(&code, true, "").unwrap();
        fresh.round1().unwrap();
        b1.values[1] = "1".into();
        assert!(fresh.round2(&b1).is_err());
    }
}
