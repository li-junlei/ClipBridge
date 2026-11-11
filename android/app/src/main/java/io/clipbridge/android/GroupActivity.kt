package io.clipbridge.android

import android.content.*
import android.graphics.Bitmap
import android.os.Bundle
import android.os.PersistableBundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.BackHandler
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import org.json.*
import top.yukonga.miuix.kmp.basic.*
import top.yukonga.miuix.kmp.theme.*
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class GroupActivity:ComponentActivity(){
    private val worker=Executors.newSingleThreadScheduledExecutor()
    private lateinit var controller:GroupController
    private var document by mutableStateOf(JSONObject())
    private var mode by mutableIntStateOf(0)
    private var busy by mutableStateOf(false)
    private var message by mutableStateOf("")
    private var groupName by mutableStateOf("我的设备")
    private var server by mutableStateOf("")
    private var username by mutableStateOf("")
    private var password by mutableStateOf("")
    private var setup by mutableStateOf("")
    private var invitation by mutableStateOf("")
    private var webdav by mutableStateOf(false)
    private var displayedCode by mutableStateOf("")
    private var safety by mutableStateOf("")
    private var peerName by mutableStateOf("")
    private var ready by mutableStateOf(false)
    private var approved by mutableStateOf(false)
    private var completed by mutableStateOf(false)
    private var inviting by mutableStateOf(false)
    private var qr by mutableStateOf<Bitmap?>(null)
    private var session:GroupSession?=null
    private var sessionFailed=false
    private var refreshAt=0L
    private var selectedGroup=""
    private val notificationPermission=registerForActivityResult(ActivityResultContracts.RequestPermission()){if(it)startReceiver()else toast("未允许通知，后台成员更新和附件接收可能停止。")}
    private val scanner=registerForActivityResult(ScanContract()){result->result.contents?.let{invitation=it;mode=2}}
    override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);controller=GroupController(this);mode=intent.getIntExtra("mode",0);selectedGroup=intent.getStringExtra("groupId")?:"";intent.getStringExtra("invitation")?.let{invitation=it;mode=2};setContent{
        val dark=isSystemInDarkTheme();val theme=remember(dark){ThemeController(colorSchemeMode=if(dark)ColorSchemeMode.Dark else ColorSchemeMode.Light)}
        MiuixTheme(controller=theme){Content()}
    };worker.execute{try{val state=controller.load(true);val legacy=intent.getStringExtra("legacyId");var seed:JSONObject?=null;if(legacy!=null){val rows=JSONArray(contentResolver.call(BridgeProvider.URI,"profiles",null,null)!!.getString("profiles"));for(i in 0 until rows.length()){val row=rows.getJSONObject(i);if(row.optString("groupId").isEmpty()&&GroupStateOps.legacyId(row)==legacy){seed=row.getJSONObject("config");break}}};val initial=seed;runOnUiThread{document=state;if(initial!=null){server=initial.getString("server");username=initial.optString("username");password=initial.optString("password");webdav=initial.optString("transport")=="webdav";mode=1}}}catch(e:Exception){runOnUiThread{message="同步组数据无法读取。"}}};worker.scheduleWithFixedDelay(::poll,1,1,TimeUnit.SECONDS)}
    override fun onNewIntent(intent:Intent){super.onNewIntent(intent);setIntent(intent);if(session!=null&&!completed){toast("请先完成或关闭当前配对。");return};intent.getStringExtra("invitation")?.let{selectedGroup="";invitation=it;mode=2}}
    private fun toast(text:String){Toast.makeText(this,text,Toast.LENGTH_LONG).show()}
    private fun startReceiver(){if(android.os.Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED){notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS);return};runCatching{startForegroundService(Intent(this,TransferService::class.java))}.onFailure{toast("请打开应用以启动后台接收。")}}
    private fun connection():JSONObject=JSONObject().put("transport",if(webdav)"webdav"else"relay").put("server",server.trim().trimEnd('/')).put("username",username).put("password",password).also{GroupCrypto.connection(it)}
    private fun operation(action:()->Unit){if(busy)return;busy=true;message="正在连接…";worker.execute{try{action()}catch(e:Exception){runOnUiThread{message=GroupNetwork.message(e,webdav)}}finally{runOnUiThread{busy=false}}}}
    private fun loadUi(){val state=controller.load(false);runOnUiThread{document=state}}
    private fun invite(group:JSONObject,identity:JSONObject){session?.close();session=GroupSession.invite(group,identity){controller.save(group);runOnUiThread{startReceiver()}};sessionFailed=false;val value=session!!;val bitmap=runCatching{makeQr(value.link)}.getOrNull();runOnUiThread{inviting=true;displayedCode=GroupPake.display(value.code);safety="";peerName="";ready=false;approved=false;completed=false;qr=bitmap;mode=3;message="邀请 5 分钟内有效，请保持此页面开启。";startReceiver()}}
    private fun create(){val name=groupName.trim();val connection=runCatching{connection()}.getOrElse{toast(it.message?:"连接信息无效");return};val code=setup;operation{val state=controller.load(true);val identity=state.getJSONObject("identity");val group=GroupCrypto.create(identity,name,connection);GroupNetwork.provision(group,code);controller.save(group);loadUi();try{GroupNetwork.publish(group,identity);group.put("publishedSequence",group.getLong("sequence"));controller.save(group);invite(group,identity)}catch(e:Exception){runOnUiThread{mode=0;message="组已保存，邀请失败："+GroupNetwork.message(e,webdav)}}}}
    private fun join(){selectedGroup="";val input=invitation.trim();val parsed=runCatching{if(input.startsWith("clipbridge://group#"))GroupNetwork.parse(input)else JSONObject().put("connection",connection()).put("code",GroupPake.normalize(input))}.getOrElse{toast(it.message?:"邀请信息无效");return};operation{val state=controller.load(true);val identity=state.getJSONObject("identity");session?.close();session=GroupSession.join(parsed.getJSONObject("connection"),parsed.getString("code"),identity){group->controller.joined(group);runOnUiThread{startReceiver()}};sessionFailed=false;runOnUiThread{inviting=false;displayedCode="";safety="";peerName="";ready=false;approved=false;completed=false;qr=null;mode=3;message="正在与邀请设备建立安全连接。";startReceiver()}}}
    private fun poll(){if(isFinishing||isDestroyed)return;try{
        val active=session;if(active!=null&&!active.done&&!sessionFailed){active.step();val status=active.status;val number=active.sas;val peer=active.peer()?.optString("name")?:"";val isReady=active.ready();val isApproved=active.approved;val done=active.done;runOnUiThread{message=status+(if(done)""else"\n剩余 ${maxOf(0,active.expires()-System.currentTimeMillis()/1000)} 秒");safety=if(number.length==6)number.take(3)+" "+number.drop(3)else"";peerName=peer;ready=isReady;approved=isApproved;completed=done};if(done){controller.tick(true);loadUi()}}
        if(System.currentTimeMillis()>refreshAt){refreshAt=System.currentTimeMillis()+2000;if(mode==0)controller.tick(false);loadUi()}
    }catch(e:Exception){if(e is SecurityException||e is org.bouncycastle.crypto.CryptoException||e is IllegalArgumentException||e is IllegalStateException){sessionFailed=true;runOnUiThread{message=e.message?:"配对验证失败，请关闭后重新生成邀请。";ready=false}}else runOnUiThread{if(mode==3)message=GroupNetwork.message(e,webdav)+" 正在重试…"}}}
    private fun approve(){approved=true;worker.execute{runCatching{session?.approve()}.onFailure{runOnUiThread{toast("请等待安全码出现。")}}}}
    private fun closeSession(){worker.execute{val active=session;if(active!=null&&!active.canClose()&&!sessionFailed){runOnUiThread{toast("正在完成入组，请等待完成后再返回。");message="正在完成入组，请保持此页面开启。"};return@execute};if(active?.done==true&&!inviting){runOnUiThread{setResult(RESULT_OK);finish()};return@execute};active?.close();session=null;sessionFailed=false;runOnUiThread{mode=0;ready=false;approved=false;qr=null;message=""}}}
    private fun copy(){val link=session?.link?:return;val clip=ClipData.newPlainText("ClipBridge",link);clip.description.extras=PersistableBundle().apply{putBoolean("android.content.extra.IS_SENSITIVE",true)};getSystemService(ClipboardManager::class.java).setPrimaryClip(clip);toast("邀请已复制，请仅交给自己的设备。")}
    private fun remove(groupId:String,member:String){android.app.AlertDialog.Builder(this).setTitle("移出此设备？").setMessage("其他成员收到通知后停止向它同步。其他配对不受影响。").setNegativeButton("取消",null).setPositiveButton("移出"){_,_->operation{val state=controller.load(false);val group=GroupStateOps.find(state,groupId)?:throw IllegalArgumentException();val identity=state.getJSONObject("identity");GroupCrypto.remove(group,identity,member);if(member==identity.getJSONObject("member").getString("id"))group.put("enabled",false);controller.save(group);try{GroupNetwork.publish(group,identity);group.put("publishedSequence",group.getLong("sequence"));controller.save(group)}catch(e:Exception){group.put("status","等待通知其他成员");controller.save(group)};loadUi();runOnUiThread{startReceiver()}}}.show()}
    private fun deleteGroup(id:String){android.app.AlertDialog.Builder(this).setTitle("删除本机记录？").setMessage("仅删除本机组记录。若退出通知尚未送达，请在其他设备上手动移除此设备。").setNegativeButton("取消",null).setPositiveButton("删除"){_,_->operation{contentResolver.call(BridgeProvider.URI,"group-delete",id,null);loadUi()}}.show()}
    private fun makeQr(link:String):Bitmap{val hints=mapOf(EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.L,EncodeHintType.MARGIN to 2);val matrix=QRCodeWriter().encode(link,BarcodeFormat.QR_CODE,700,700,hints);val pixels=IntArray(700*700){if(matrix[it%700,it/700])android.graphics.Color.BLACK else android.graphics.Color.WHITE};return Bitmap.createBitmap(pixels,700,700,Bitmap.Config.ARGB_8888)}
    @Composable private fun Field(title:String,value:String,secret:Boolean=false,change:(String)->Unit){val colors=MiuixTheme.colorScheme;Column(verticalArrangement=Arrangement.spacedBy(8.dp)){Text(title,fontSize=13.sp,color=colors.onSurface.copy(alpha=.65f));BasicTextField(value=value,onValueChange=change,singleLine=true,textStyle=TextStyle(color=colors.onSurface,fontSize=16.sp),visualTransformation=if(secret)PasswordVisualTransformation()else VisualTransformation.None,modifier=Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(colors.onSurface.copy(alpha=.06f)).padding(14.dp))}}
    @Composable private fun Content(){BackHandler(enabled=mode!=0||busy){if(busy)toast("正在连接，请稍候。")else if(mode==3)closeSession()else mode=0};val colors=MiuixTheme.colorScheme;val titles=listOf("同步组","创建同步组","加入同步组","设备配对");Scaffold(topBar={TopAppBar(title=titles[mode],largeTitle=titles[mode])}){padding->Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal=20.dp),verticalArrangement=Arrangement.spacedBy(14.dp)){
        if(message.isNotBlank())Text(message,fontSize=13.sp,color=colors.onSurface.copy(alpha=.75f))
        if(mode==0){if(selectedGroup.isEmpty()){Row(horizontalArrangement=Arrangement.spacedBy(12.dp)){Button(onClick={mode=1},modifier=Modifier.weight(1f)){Text("创建同步组")};Button(onClick={mode=2},modifier=Modifier.weight(1f)){Text("加入同步组")}};TextButton(text="扫描邀请二维码",onClick={scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("扫描已有设备的同步组邀请").setBeepEnabled(false))})};val groups=document.optJSONArray("groups")?:JSONArray();val self=document.optJSONObject("identity")?.optJSONObject("member")?.optString("id")?:"";
            for(i in 0 until groups.length()){val group=groups.getJSONObject(i);val id=group.getString("id");if(selectedGroup.isNotEmpty()&&id!=selectedGroup)continue;val members=group.getJSONObject("members");val removed=group.getJSONArray("removed");Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)){Text(group.getString("name"),fontSize=20.sp,fontWeight=FontWeight.Bold);Text(group.optString("status","等待连接"),fontSize=12.sp,color=colors.onSurface.copy(alpha=.6f));for(memberId in GroupCrypto.keys(members).sorted()){if(GroupCrypto.contains(removed,memberId))continue;val member=members.getJSONObject(memberId).getJSONObject("member");Row(verticalAlignment=Alignment.CenterVertically){Text(member.getString("name")+(if(memberId==self)"（本机）"else""),Modifier.weight(1f),fontSize=15.sp);if(memberId!=self&&group.optJSONArray("suppressedPeers")?.let{GroupCrypto.contains(it,memberId)}==true)TextButton(text="连接",onClick={operation{controller.connect(id,memberId);loadUi()}});TextButton(text=if(memberId==self)"退出"else"移出",onClick={remove(id,memberId)})}};if(group.optBoolean("enabled",true)&&!GroupCrypto.contains(removed,self))Button(onClick={operation{val state=controller.load(false);invite(GroupStateOps.find(state,id)!!,state.getJSONObject("identity"))}}){Text("邀请设备")}else TextButton(text="删除本机记录",onClick={deleteGroup(id)})}}}
            Text("最多 8 台设备。任何成员都可邀请新设备；扫码或配对码加入后，请核对两端安全码。",fontSize=12.sp,color=colors.onSurface.copy(alpha=.6f));TextButton(text="返回",onClick={finish()})
        }
        if(mode==1||mode==2){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(16.dp)){Row(horizontalArrangement=Arrangement.spacedBy(10.dp)){TextButton(text=if(!webdav)"✓ 专用中转"else"专用中转",onClick={webdav=false});TextButton(text=if(webdav)"✓ WebDAV"else"WebDAV",onClick={webdav=true})};Field("服务器 / WebDAV 目录",server){server=it};if(webdav){Field("WebDAV 用户名",username){username=it};Field("WebDAV 应用密码",password,true){password=it}};if(mode==1){Field("同步组名称",groupName){groupName=it};if(!webdav)Field("服务器管理码",setup,true){setup=it}}else Field("配对码或完整邀请",invitation){invitation=it};Text("本机："+(document.optJSONObject("identity")?.optJSONObject("member")?.optString("name")?:""),fontSize=12.sp,color=colors.onSurface.copy(alpha=.6f));Text(if(webdav)"手动配对需先填写同一 WebDAV 账号；扫码或粘贴邀请可读取连接信息。"else"只有创建同步组需要服务器管理码。",fontSize=12.sp,color=colors.onSurface.copy(alpha=.6f))}};Button(onClick={if(!busy){if(mode==1)create()else join()}},modifier=Modifier.fillMaxWidth()){Text(if(busy)"正在连接…"else if(mode==1)"创建并邀请设备"else"连接邀请")};TextButton(text="返回",onClick={if(!busy)mode=0})}
        if(mode==3){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.fillMaxWidth().padding(20.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(14.dp)){if(inviting){qr?.let{Image(it.asImageBitmap(),"邀请二维码",Modifier.size(245.dp))};Text(displayedCode,fontSize=22.sp,fontWeight=FontWeight.Bold);TextButton(text="复制邀请",onClick=::copy)};if(peerName.isNotBlank())Text(peerName,fontSize=19.sp,fontWeight=FontWeight.Bold);if(safety.isNotBlank()){Text("核对两端安全码",fontSize=13.sp,color=colors.onSurface.copy(alpha=.6f));Text(safety,fontSize=36.sp,fontWeight=FontWeight.Bold)};if(ready&&!approved)Button(onClick=::approve){Text("安全码一致，确认")};if(approved&&!completed)Text("正在完成入组，请勿关闭页面",fontSize=13.sp)}};TextButton(text=if(completed)"完成"else if(approved)"正在完成入组"else"关闭",onClick=::closeSession)}
        Spacer(Modifier.height(22.dp))
    }}}
    override fun onDestroy(){session?.close();session=null;worker.shutdownNow();super.onDestroy()}
}
