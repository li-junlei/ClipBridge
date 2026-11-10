namespace ClipBridge;
public static class GroupRuntime
{
    public static async Task<bool> Refresh(DeviceGroup group,GroupIdentity identity,CancellationToken ct,bool force=false)
    {
        Groups.Validate(group);long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();bool changed=false;
        using var store=new GroupStore(group);
        if(group.PublishedSequence!=group.Sequence&&group.PublishRetryAfter<=now){try{await store.Publish(identity,ct);group.PublishRetryAfter=0;changed=true;}catch{group.PublishRetryAfter=now+30;throw;}}
        if(!group.Enabled||group.Removed.Contains(identity.Member.Id)){group.Status=group.Removed.Contains(identity.Member.Id)?"已退出同步组":"同步已关闭";return changed;}
        if(!force&&group.CheckedAt>now-60)return changed;
        group.CheckedAt=now;
        foreach(var id in group.Members.Keys.ToArray()){
            if(id==identity.Member.Id||group.Removed.Contains(id))continue;
            var feed=await store.Read(id,ct);if(feed is not null)changed|=Groups.Accept(group,feed);
        }
        group.Status="成员清单已更新 · "+group.Members.Keys.Count(id=>!group.Removed.Contains(id))+" 台设备";return true;
    }
    public static List<BridgeProfile> Channels(DeviceGroup group,GroupIdentity identity){if(!group.Enabled||group.Removed.Contains(identity.Member.Id))return [];return group.Members.Values.Where(c=>c.Member.Id!=identity.Member.Id&&!group.Removed.Contains(c.Member.Id)&&!group.SuppressedPeers.Contains(c.Member.Id)).Select(c=>new BridgeProfile{Config=Groups.Channel(group,identity,c.Member),GroupId=group.Id,GroupPeer=c.Member.Id,GroupKey=Groups.Hash(Groups.Bytes(c.Member.SigningKey)),Name=c.Member.Name,PeerIdentity=c.Member.Id}).ToList();}
}
