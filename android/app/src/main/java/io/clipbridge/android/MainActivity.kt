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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import org.json.JSONArray
import org.json.JSONObject
import top.yukonga.miuix.kmp.basic.*
import top.yukonga.miuix.kmp.theme.ColorSchemeMode
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.ThemeController
import java.util.concurrent.Executors
data class ProfileUi(val pair:String,val name:String,val server:String,val enabled:Boolean,val status:String,val last:Long,val group:String)
class MainActivity:ComponentActivity(){
    private var profiles by mutableStateOf(listOf<ProfileUi>())
    private var paused by mutableStateOf(false)
    private var module by mutableStateOf(false)
    private var hidden by mutableStateOf(false)
    private var busy by mutableStateOf(false)
    private var message by mutableStateOf("")
    private val io=Executors.newSingleThreadExecutor()
    private val notificationPermission=registerForActivityResult(ActivityResultContracts.RequestPermission()){if(it)startTransfers()else toast("未允许通知，文件确认和图片预览无法显示。")}
    private val scanner=registerForActivityResult(ScanContract()){result->result.contents?.let(::pair)}
    private val picker=registerForActivityResult(ActivityResultContracts.OpenDocument()){uri->if(uri!=null)runCatching{val bytes=contentResolver.openInputStream(uri)!!.use{it.readNBytes(8193)};require(bytes.size<=8192);contentResolver.call(BridgeProvider.URI,"import",String(bytes,Charsets.UTF_8),null);refresh();startTransfers()}.onFailure{toast("无法导入配置，请检查文件。")}}
    override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);refresh();hidden=isHidden();if(android.os.Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)else startTransfers()
        setContent{val dark=isSystemInDarkTheme();SideEffect{androidx.core.view.WindowCompat.getInsetsController(window,window.decorView).isAppearanceLightStatusBars=!dark};val theme=remember(dark){ThemeController(colorSchemeMode=if(dark)ColorSchemeMode.Dark else ColorSchemeMode.Light)};MiuixTheme(controller=theme){LaunchedEffect(Unit){while(true){refresh();delay(2000)}};App()}}
        intent?.dataString?.let(::pair)
    }
    override fun onNewIntent(intent:Intent){super.onNewIntent(intent);setIntent(intent);intent.dataString?.let(::pair)}
    private fun refresh(){runCatching{val b=contentResolver.call(BridgeProvider.URI,"profiles",null,null)!!;val list=JSONArray(b.getString("profiles"));profiles=(0 until list.length()).map{val p=list.getJSONObject(it);val c=p.getJSONObject("config");ProfileUi(c.getString("pair"),p.optString("name").ifBlank{"等待设备名称"},c.getString("server"),p.optBoolean("enabled",true),p.optString("status","等待连接"),p.optLong("lastSync"),p.optString("peerIdentity").ifBlank{c.getString("pair")})};paused=b.getBoolean("paused");module=System.currentTimeMillis()-b.getLong("moduleSeenAt")<18000}}
    private fun startTransfers(){if(!paused&&profiles.any{it.enabled}&&(android.os.Build.VERSION.SDK_INT<33||checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)==android.content.pm.PackageManager.PERMISSION_GRANTED))runCatching{startForegroundService(Intent(this,TransferService::class.java))}.onFailure{toast("附件接收未启动，请重新打开应用。")}}
    private fun toggle(p:ProfileUi,value:Boolean){runCatching{contentResolver.call(BridgeProvider.URI,"toggle",p.pair,Bundle().apply{putBoolean("enabled",value)});refresh();startTransfers()}.onFailure{toast("保存配置开关失败。")}}
    private fun remove(p:ProfileUi){AlertDialog.Builder(this).setTitle("删除此配置？").setMessage("仅停止这套配置，其他配置不受影响。").setNegativeButton("取消",null).setPositiveButton("删除"){_,_->runCatching{contentResolver.call(BridgeProvider.URI,"delete",p.pair,null);refresh()}.onFailure{toast("删除失败。")}}.show()}
    private fun scan(){if(!busy)scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("扫描电脑配对二维码").setOrientationLocked(false).setBeepEnabled(false))}
    private fun pair(qr:String){val server=runCatching{PairingClient.server(qr)}.getOrElse{toast("无效的配对二维码。");return};AlertDialog.Builder(this).setTitle("添加配置").setMessage("服务器：$server\n\n现有配置会保留。请确认二维码来自自己的电脑。").setNegativeButton("取消",null).setPositiveButton("添加"){_,_->busy=true;io.execute{try{val config=PairingClient.claim(qr);contentResolver.call(BridgeProvider.URI,"import",config,null);runOnUiThread{busy=false;message="配置已添加";refresh();startTransfers()}}catch(e:Exception){runOnUiThread{busy=false;message="添加失败，请检查二维码、服务器或是否重复导入同一配对。"}}}}.show()}
    private fun isHidden()=packageManager.getComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"))==android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED
    private fun hide(value:Boolean){fun apply(){runCatching{packageManager.setComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"),if(value)android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED else android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_ENABLED,android.content.pm.PackageManager.DONT_KILL_APP);hidden=value}.onFailure{toast("无法修改图标设置。")}};if(value)AlertDialog.Builder(this).setTitle("隐藏桌面图标？").setMessage("隐藏后，可从 LSPosed / Vector 模块管理器中打开 ClipBridge。").setNegativeButton("取消",null).setPositiveButton("隐藏"){_,_->apply()}.show()else apply()}
    private fun toast(text:String){Toast.makeText(this,text,Toast.LENGTH_LONG).show()}
    override fun onDestroy(){io.shutdown();super.onDestroy()}
    @Composable private fun App(){var tab by remember{mutableIntStateOf(0)};val color=MiuixTheme.colorScheme;val titles=listOf("ClipBridge","配置","设置");Scaffold(topBar={TopAppBar(title=titles[tab],largeTitle=titles[tab])},bottomBar={NavigationBar(modifier=Modifier.navigationBarsPadding(),showDivider=false){NavigationBarItem(tab==0,{tab=0},Icons.Rounded.Home,"首页");NavigationBarItem(tab==1,{tab=1},Icons.Rounded.Person,"配置");NavigationBarItem(tab==2,{tab=2},Icons.Rounded.Settings,"设置")}}){padding->Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal=20.dp),verticalArrangement=Arrangement.spacedBy(14.dp)){
            if(tab==0){Row(Modifier.fillMaxWidth().height(184.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)){Column(Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(26.dp)).background(if(module)Color(0xFFDFF8E5)else Color(0xFFFFE4E4)).padding(20.dp),verticalArrangement=Arrangement.Center){Text(if(module)"模块已激活"else"模块未激活",fontSize=21.sp,fontWeight=FontWeight.Bold,color=if(module)Color(0xFF145D2C)else Color(0xFF9F2525));Spacer(Modifier.height(10.dp));Text(if(module)"模块服务已连接"else"请检查模块管理器",fontSize=13.sp,color=if(module)Color(0xFF356B44)else Color(0xFFA04747))};Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(12.dp)){Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(18.dp)){Text("同步状态",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Text(if(paused)"已暂停"else if(profiles.none{it.enabled})"未开启配置"else"已开启 ${profiles.count{it.enabled}} 套",fontSize=16.sp)}};Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(18.dp)){Text("接收设备",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Text(profiles.filter{it.enabled}.map{it.group}.distinct().size.toString(),fontSize=27.sp,fontWeight=FontWeight.Bold)}}}}
                val last=profiles.maxOfOrNull{it.last}?:0L;Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Info("最后同步",if(last==0L)"暂无同步记录"else java.text.SimpleDateFormat("MM-dd HH:mm:ss",java.util.Locale.getDefault()).format(java.util.Date(last)))}
            }
            if(tab<2){for(p in profiles){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp)){Row(verticalAlignment=Alignment.CenterVertically){Text(p.name,Modifier.weight(1f),fontSize=18.sp,fontWeight=FontWeight.Bold);Switch(p.enabled,{toggle(p,it)})};Text(p.server,fontSize=12.sp,color=color.onSurface.copy(alpha=.55f));Text(if(p.enabled)p.status else"已关闭",fontSize=13.sp,modifier=Modifier.padding(top=6.dp));Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.End){TextButton(text="删除",onClick={remove(p)})}}}};Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){BasicComponent(title=if(busy)"正在添加…"else"扫描添加配置",onClick=::scan);BasicComponent(title="导入配置",onClick={picker.launch(arrayOf("application/json"))})};if(message.isNotBlank())Text(message,fontSize=13.sp)}
            if(tab==2){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Toggle("暂停全部同步",paused){value->runCatching{contentResolver.call(BridgeProvider.URI,"pause",value.toString(),null);refresh();startTransfers()}};Toggle("隐藏桌面图标",hidden,::hide);BasicComponent(title="开启附件接收通知",onClick={if(android.os.Build.VERSION.SDK_INT>=33)notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)else startTransfers()});BasicComponent(title="取消待发送附件",onClick={startForegroundService(Intent(this@MainActivity,TransferService::class.java).putExtra("action","cancel"))})};Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Info("版本","0.5.0");Info("文件目录","Download/ClipBridge");Info("图片相册","Pictures/ClipBridge")}}
            Spacer(Modifier.height(12.dp))
        }}}
    @Composable private fun Toggle(title:String,checked:Boolean,change:(Boolean)->Unit){Row(Modifier.fillMaxWidth().padding(20.dp),verticalAlignment=Alignment.CenterVertically){Text(title,Modifier.weight(1f),fontSize=16.sp);Switch(checked,change)}}
    @Composable private fun Info(title:String,value:String){Row(Modifier.fillMaxWidth().padding(20.dp),horizontalArrangement=Arrangement.spacedBy(14.dp)){Text(title,fontSize=14.sp,modifier=Modifier.width(76.dp));Text(value,fontSize=14.sp,color=MiuixTheme.colorScheme.onSurface.copy(alpha=.6f),modifier=Modifier.weight(1f))}}
}
