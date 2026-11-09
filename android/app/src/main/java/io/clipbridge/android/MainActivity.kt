package io.clipbridge.android

import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import org.json.JSONObject
import top.yukonga.miuix.kmp.basic.*
import top.yukonga.miuix.kmp.theme.ColorSchemeMode
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.ThemeController
import java.util.concurrent.Executors

data class UiSnapshot(val paired:Boolean=false,val server:String="",val device:String="",val peer:String="",val paused:Boolean=false,val module:Boolean=false,val status:String="等待模块启动",val peerName:String="",val lastSync:Long=0)

class MainActivity : ComponentActivity() {
    private var snapshot by mutableStateOf(UiSnapshot())
    private var hidden by mutableStateOf(false)
    private var busy by mutableStateOf(false)
    private var message by mutableStateOf("")
    private val io=Executors.newSingleThreadExecutor()
    private val scanner=registerForActivityResult(ScanContract()){ result -> result.contents?.let(::confirmPairing) }
    private val filePicker=registerForActivityResult(ActivityResultContracts.OpenDocument()){ uri ->
        if(uri!=null) runCatching {
            val bytes=contentResolver.openInputStream(uri)!!.use{input->
                val out=java.io.ByteArrayOutputStream();val buffer=ByteArray(1024)
                while(true){val n=input.read(buffer);if(n<0)break;out.write(buffer,0,n);require(out.size()<=8192)}
                out.toByteArray()
            }
            require(bytes.size<=8192)
            contentResolver.call(BridgeProvider.URI,"import",String(bytes,Charsets.UTF_8),null)
            refresh();toast("配对配置已导入")
        }.onFailure{toast("导入失败，请检查文件或使用扫码配对")}
    }
    override fun onCreate(savedInstanceState:Bundle?){
        super.onCreate(savedInstanceState);refresh();hidden=isHidden()
        setContent {
            val dark=isSystemInDarkTheme()
            SideEffect { androidx.core.view.WindowCompat.getInsetsController(window,window.decorView).isAppearanceLightStatusBars = !dark }
            val controller=remember(dark){ThemeController(colorSchemeMode=if(dark) ColorSchemeMode.Dark else ColorSchemeMode.Light)}
            MiuixTheme(controller=controller){
                LaunchedEffect(Unit){while(true){refresh();delay(3000)}}
                BridgeApp(snapshot,busy,message,hidden,onHide=::hideIcon,onDelete=::deletePairing,onScan=::scan,onImport={filePicker.launch(arrayOf("application/json"))},onPause=::pause)
            }
        }
        intent?.dataString?.let(::confirmPairing)
    }
    override fun onNewIntent(intent:Intent){super.onNewIntent(intent);setIntent(intent);intent.dataString?.let(::confirmPairing)}
    private fun refresh(){
        runCatching {
            val b=contentResolver.call(BridgeProvider.URI,"load",null,null)!!
            val status=contentResolver.call(BridgeProvider.URI,"status",null,null)!!
            val c=b.getString("config")?.let{JSONObject(it)}
            snapshot=UiSnapshot(c!=null,c?.optString("server")?:"",c?.optString("device")?:"",c?.optString("peer")?:"",b.getBoolean("paused"),System.currentTimeMillis()-b.getLong("moduleSeenAt",0)<18000,status.getString("status")?:"等待模块启动",b.getString("peerName")?:"",b.getLong("lastSync",0))
        }
    }
    private fun pause(value:Boolean){runCatching{contentResolver.call(BridgeProvider.URI,"pause",value.toString(),null);refresh()}.onFailure{toast("无法保存同步设置")}}
    private fun scan(){
        scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("扫描电脑上 ClipBridge 的配对二维码").setBeepEnabled(false).setOrientationLocked(false))
    }
    private fun confirmPairing(qr:String){
        val server=runCatching{PairingClient.server(qr)}.getOrElse{toast(it.message?:"无效的二维码");return}
        AlertDialog.Builder(this).setTitle(if(snapshot.paired) "切换设备配对？" else "连接到电脑")
            .setMessage("服务器：$server\n\n"+if(snapshot.paired) "当前配对将切换到此电脑。确认这是你电脑上生成的二维码后继续。" else "确认这是你电脑上生成的二维码，即可安全传递配对信息。")
            .setNegativeButton("取消",null).setPositiveButton("确认配对"){_,_->
                busy=true;message=if(qr.startsWith("clipbridge://webdav#")) "正在导入 WebDAV 配置…" else "正在获取加密邀请…"
                io.execute {
                    try {
                        val config=PairingClient.claim(qr)
                        contentResolver.call(BridgeProvider.URI,if(snapshot.paired) "replace" else "import",config,null)
                        runOnUiThread{busy=false;message="配对成功，正在连接电脑";refresh();toast("配对成功")}
                    }catch(e:Exception){runOnUiThread{busy=false;message=when(e){is IllegalArgumentException -> e.message?:"配对邀请无效";else -> "连接失败，请检查服务器和网络后刷新二维码"};toast(message)}}
                }
            }.show()
    }
    private fun isHidden()=packageManager.getComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"))==android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED
    private fun hideIcon(value:Boolean){
        fun apply(){runCatching{packageManager.setComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"),if(value) android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED else android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_ENABLED,android.content.pm.PackageManager.DONT_KILL_APP);hidden=value}.onFailure{toast("无法修改桌面图标设置")}}
        if(value)AlertDialog.Builder(this).setTitle("隐藏桌面图标？").setMessage("隐藏后，可从 LSPosed / Vector 模块管理器中打开 ClipBridge。").setNegativeButton("取消",null).setPositiveButton("隐藏"){_,_->apply()}.show() else apply()
    }
    private fun deletePairing(){AlertDialog.Builder(this).setTitle("删除配置？").setMessage("删除本机配对配置并停止同步。对方设备的配置不会被删除。").setNegativeButton("取消",null).setPositiveButton("删除"){_,_->runCatching{contentResolver.call(BridgeProvider.URI,"delete",null,null);message="";refresh()}.onFailure{toast("删除失败，请重试")}}.show()}
    private fun toast(text:String){Toast.makeText(this,text,Toast.LENGTH_LONG).show()}
    override fun onDestroy(){io.shutdown();super.onDestroy()}
}

@Composable
private fun BridgeApp(s:UiSnapshot,busy:Boolean,message:String,hidden:Boolean,onHide:(Boolean)->Unit,onDelete:()->Unit,onScan:()->Unit,onImport:()->Unit,onPause:(Boolean)->Unit){
    var tab by remember{mutableIntStateOf(0)}
    val color=MiuixTheme.colorScheme
    val titles=listOf("ClipBridge","设备","设置")
    Scaffold(topBar={TopAppBar(title=titles[tab],largeTitle=titles[tab])},bottomBar={
        NavigationBar(modifier=Modifier.navigationBarsPadding(),showDivider=false){
            NavigationBarItem(selected=tab==0,onClick={tab=0},icon=Icons.Rounded.Home,label="首页")
            NavigationBarItem(selected=tab==1,onClick={tab=1},icon=Icons.Rounded.Person,label="设备")
            NavigationBarItem(selected=tab==2,onClick={tab=2},icon=Icons.Rounded.Settings,label="设置")
        }
    }){padding ->
        Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal=20.dp),verticalArrangement=Arrangement.spacedBy(14.dp)){
            if(tab==0){
                Row(Modifier.fillMaxWidth().height(188.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)){
                    Column(Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(26.dp)).background(if(s.module)Color(0xFFDFF8E5) else Color(0xFFFFE4E4)).padding(20.dp),verticalArrangement=Arrangement.Center){
                        Text(if(s.module)"模块已激活" else "模块未激活",fontSize=21.sp,fontWeight=FontWeight.Bold,color=if(s.module)Color(0xFF145D2C) else Color(0xFF9F2525))
                        Spacer(Modifier.height(10.dp));Text(if(s.module)"模块服务已连接" else "请检查模块管理器",fontSize=13.sp,color=if(s.module)Color(0xFF356B44) else Color(0xFFA04747))
                    }
                    Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(12.dp)){
                        Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(horizontal=18.dp,vertical=12.dp)){
                            Text("同步状态",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Spacer(Modifier.height(5.dp));Text(if(!s.paired)"未配对" else if(!s.module)"未运行" else if(s.paused)"已暂停" else s.status,fontSize=16.sp,fontWeight=FontWeight.Medium,maxLines=2)
                        }}
                        Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(horizontal=18.dp,vertical=12.dp)){
                            Text("配对设备",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Text(if(s.paired)"1" else "0",fontSize=27.sp,fontWeight=FontWeight.Bold)
                        }}
                    }
                }
                Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){InfoRow("最后同步",if(s.lastSync==0L)"暂无同步记录" else java.text.SimpleDateFormat("yyyy-MM-dd HH:mm:ss",java.util.Locale.getDefault()).format(java.util.Date(s.lastSync)))}
                if(s.paired){DeviceCard(s);Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){ToggleRow("自动同步",!s.paused){onPause(!it)}}}
                else Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){ActionRow(if(busy)"正在配对…" else "扫描配对二维码"){if(!busy)onScan()}}
                if(message.isNotBlank())Text(message,fontSize=13.sp,color=color.onSurface.copy(alpha=.65f),modifier=Modifier.padding(8.dp))
            }
            if(tab==1){
                if(s.paired)DeviceCard(s)
                Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){ActionRow("扫描配对二维码"){if(!busy)onScan()};ActionRow("导入配置",onImport);if(s.paired)ActionRow("删除配置",onDelete)}
            }
            if(tab==2){
                Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){ToggleRow("暂停同步",s.paused,onPause);ToggleRow("隐藏桌面图标",hidden,onHide)}
                Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){InfoRow("版本","0.4.0");InfoRow("设备名称",android.os.Build.MODEL)}
            }
            Spacer(Modifier.height(12.dp))
        }
    }
}
@Composable private fun ToggleRow(title:String,checked:Boolean,onChange:(Boolean)->Unit){Row(Modifier.fillMaxWidth().padding(20.dp),verticalAlignment=Alignment.CenterVertically){Text(title,Modifier.weight(1f),fontSize=16.sp);Switch(checked=checked,onCheckedChange=onChange)}}
@Composable private fun DeviceCard(s:UiSnapshot){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp)){Text(s.peerName.ifBlank{"等待设备名称"},fontSize=19.sp,fontWeight=FontWeight.Bold);Spacer(Modifier.height(8.dp));Text(s.server,fontSize=13.sp,color=MiuixTheme.colorScheme.onSurface.copy(alpha=.55f),maxLines=2,overflow=TextOverflow.Ellipsis)}}}
@Composable private fun ActionRow(title:String,onClick:()->Unit){BasicComponent(title=title,onClick=onClick,endActions={Text("›",fontSize=24.sp,color=MiuixTheme.colorScheme.onSurface.copy(alpha=.35f))})}
@Composable private fun InfoRow(title:String,value:String){Row(Modifier.fillMaxWidth().padding(20.dp),horizontalArrangement=Arrangement.spacedBy(14.dp)){Text(title,fontSize=14.sp,modifier=Modifier.width(70.dp));Text(value,fontSize=14.sp,color=MiuixTheme.colorScheme.onSurface.copy(alpha=.6f),modifier=Modifier.weight(1f))}}
