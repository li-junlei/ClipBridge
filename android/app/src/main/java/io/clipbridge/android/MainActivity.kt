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
data class ProfileUi(val pair:String,val name:String,val server:String,val enabled:Boolean,val status:String,val last:Long,val group:String,val webdav:Boolean,val groupId:String)
class MainActivity:ComponentActivity(){
    private var profiles by mutableStateOf(listOf<ProfileUi>())
    private var groups by mutableStateOf(listOf<JSONObject>())
    private var hasGroups by mutableStateOf(false)
    private var textPollSeconds by mutableIntStateOf(2)
    private var attachmentPollSeconds by mutableIntStateOf(3)
    private var paused by mutableStateOf(false)
    private var module by mutableStateOf(false)
    private var hidden by mutableStateOf(false)
    private var statusNotificationsVisible by mutableStateOf(true)
    private var busy by mutableStateOf(false)
    private var message by mutableStateOf("")
    private val io=Executors.newSingleThreadExecutor()
    private val notificationPermission=registerForActivityResult(ActivityResultContracts.RequestPermission()){if(it)startTransfers()else toast("未允许通知，文件确认和图片预览无法显示。")}
    private val scanner=registerForActivityResult(ScanContract()){result->result.contents?.let(::pair)}
    override fun onCreate(savedInstanceState:Bundle?){super.onCreate(savedInstanceState);refresh();hidden=isHidden();if(android.os.Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)else startTransfers()
        setContent{val dark=isSystemInDarkTheme();SideEffect{androidx.core.view.WindowCompat.getInsetsController(window,window.decorView).isAppearanceLightStatusBars=!dark};val theme=remember(dark){ThemeController(colorSchemeMode=if(dark)ColorSchemeMode.Dark else ColorSchemeMode.Light)};MiuixTheme(controller=theme){LaunchedEffect(Unit){while(true){refresh();delay(2000)}};App()}}
        intent?.dataString?.let(::pair)
    }
    override fun onResume(){super.onResume();refresh();startTransfers();statusNotificationsVisible=StatusNotifications.visible(this)}
    private fun statusNotificationSettings(){runCatching{startActivity(StatusNotifications.settings(this))}.onFailure{toast("请在系统通知设置中调整“附件接收常驻提示”分类。")}}
    override fun onNewIntent(intent:Intent){super.onNewIntent(intent);setIntent(intent);intent.dataString?.let(::pair)}
    private fun refresh(){statusNotificationsVisible=StatusNotifications.visible(this);runCatching{val b=contentResolver.call(BridgeProvider.URI,"profiles",null,null)!!;val list=JSONArray(b.getString("profiles"));profiles=(0 until list.length()).map{val p=list.getJSONObject(it);val c=p.getJSONObject("config");ProfileUi(c.getString("pair"),p.optString("name").ifBlank{"等待设备名称"},c.getString("server"),p.optBoolean("enabled",true),p.optString("status","等待连接"),p.optLong("lastSync"),(if(p.optString("groupKey").isNotBlank())"trusted:"+p.getString("groupKey")else p.optString("peerIdentity").ifBlank{c.getString("pair")}),c.optString("transport")=="webdav",p.optString("groupId"))};val overview=JSONArray(contentResolver.call(BridgeProvider.URI,"group-overview",null,null)!!.getString("groups"));groups=(0 until overview.length()).map{overview.getJSONObject(it)};hasGroups=b.getBoolean("hasGroups");textPollSeconds=b.getInt("textPollSeconds",2);attachmentPollSeconds=b.getInt("attachmentPollSeconds",3);paused=b.getBoolean("paused");module=System.currentTimeMillis()-b.getLong("moduleSeenAt")<18000}}
    private fun startTransfers(){if(!paused&&(profiles.any{it.enabled}||hasGroups)&&(android.os.Build.VERSION.SDK_INT<33||checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)==android.content.pm.PackageManager.PERMISSION_GRANTED))runCatching{startForegroundService(Intent(this,TransferService::class.java))}.onFailure{toast("附件接收未启动，请重新打开应用。")}}
    private fun toggleGroup(group:JSONObject,value:Boolean){runCatching{contentResolver.call(BridgeProvider.URI,"group-toggle",group.getString("id"),Bundle().apply{putBoolean("enabled",value)});refresh();startTransfers()}.onFailure{toast("同步组开关保存失败。")}}
    private fun openGroup(mode:Int=0,group:JSONObject?=null){val intent=Intent(this,GroupActivity::class.java).putExtra("mode",mode);if(group!=null){if(group.optBoolean("legacy"))intent.putExtra("legacyId",group.getString("id"))else intent.putExtra("groupId",group.getString("id"))};startActivity(intent)}
    private fun deleteLegacy(group:JSONObject){AlertDialog.Builder(this).setTitle("删除双设备同步组？").setMessage("仅删除本机连接，其他同步组不受影响。").setNegativeButton("取消",null).setPositiveButton("删除"){_,_->runCatching{contentResolver.call(BridgeProvider.URI,"group-delete",group.getString("id"),null);refresh()}.onFailure{toast("删除失败。")}}.show()}
    private fun cleanHiddenGroups(){runCatching{val result=JSONObject(requireNotNull(contentResolver.call(BridgeProvider.URI,"group-cleanup-preview",null,null)!!.getString("result")));val count=result.getInt("connections");if(count==0){toast("没有发现未显示的同步组残留。");return};AlertDialog.Builder(this).setTitle("清理未显示的同步组？").setMessage("发现 ${result.getJSONArray("groups").length()} 个未显示的同步组，共 $count 条残留连接。清理后停止这些连接的后台同步，已显示的同步组和配对保持不变。其他设备上的成员记录需在对应设备管理成员中移除。").setNegativeButton("取消",null).setPositiveButton("清理"){_,_->runCatching{val removed=contentResolver.call(BridgeProvider.URI,"group-cleanup",null,null)!!.getInt("cleaned");refresh();toast("已清理 $removed 条残留连接。");startTransfers()}.onFailure{toast("清理失败，请重试。")}}.show()}.onFailure{toast("无法读取同步组数据。")}}
    private fun scan(){scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE).setPrompt("扫描同步组邀请二维码").setOrientationLocked(false).setBeepEnabled(false))}
    private fun pair(qr:String){if(qr.startsWith("clipbridge://group#"))startActivity(Intent(this,GroupActivity::class.java).putExtra("invitation",qr))else toast("请在另一台设备的同步组中生成新版邀请二维码。")}
    private fun isHidden()=packageManager.getComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"))==android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED
    private fun hide(value:Boolean){fun apply(){runCatching{packageManager.setComponentEnabledSetting(android.content.ComponentName(this,"io.clipbridge.android.Launcher"),if(value)android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED else android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_ENABLED,android.content.pm.PackageManager.DONT_KILL_APP);hidden=value}.onFailure{toast("无法修改图标设置。")}};if(value)AlertDialog.Builder(this).setTitle("隐藏桌面图标？").setMessage("隐藏后，可从 LSPosed / Vector 模块管理器中打开 ClipBridge。").setNegativeButton("取消",null).setPositiveButton("隐藏"){_,_->apply()}.show()else apply()}
    private fun toast(text:String){Toast.makeText(this,text,Toast.LENGTH_LONG).show()}
    override fun onDestroy(){io.shutdown();super.onDestroy()}
    private fun time(value:Long)=if(value==0L)"暂无同步记录"else java.text.SimpleDateFormat("MM-dd HH:mm:ss",java.util.Locale.getDefault()).format(java.util.Date(value))
    @Composable private fun App(){var tab by remember{mutableIntStateOf(0)};val color=MiuixTheme.colorScheme;val titles=listOf("ClipBridge","同步组","设置");Scaffold(topBar={TopAppBar(title=titles[tab],largeTitle=titles[tab])},bottomBar={NavigationBar(modifier=Modifier.navigationBarsPadding(),showDivider=false){NavigationBarItem(tab==0,{tab=0},Icons.Rounded.Home,"首页");NavigationBarItem(tab==1,{tab=1},Icons.Rounded.Person,"同步组");NavigationBarItem(tab==2,{tab=2},Icons.Rounded.Settings,"设置")}}){padding->Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal=20.dp),verticalArrangement=Arrangement.spacedBy(14.dp)){
        if(tab==0){Row(Modifier.fillMaxWidth().height(184.dp),horizontalArrangement=Arrangement.spacedBy(12.dp)){Column(Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(26.dp)).background(if(module)Color(0xFFDFF8E5)else Color(0xFFFFE4E4)).padding(20.dp),verticalArrangement=Arrangement.Center){Text(if(module)"模块已激活"else"模块未激活",fontSize=21.sp,fontWeight=FontWeight.Bold,color=if(module)Color(0xFF145D2C)else Color(0xFF9F2525));Spacer(Modifier.height(10.dp));Text(if(module)"模块服务已连接"else"请检查模块管理器",fontSize=13.sp,color=if(module)Color(0xFF356B44)else Color(0xFFA04747))};Column(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(12.dp)){Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(18.dp)){Text("同步状态",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Text(if(paused)"已暂停"else if(groups.none{it.optBoolean("enabled")})"未开启同步组"else"已开启 ${groups.count{it.optBoolean("enabled")}} 组",fontSize=16.sp)}};Card(Modifier.weight(1f).fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(18.dp)){Text("接收设备",fontSize=13.sp,color=color.onSurface.copy(alpha=.55f));Text(profiles.filter{it.enabled}.map{it.group}.distinct().size.toString(),fontSize=27.sp,fontWeight=FontWeight.Bold)}}}}
            Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Info("最后同步",time(groups.maxOfOrNull{it.optLong("lastSync")}?:0L))}
            for(g in groups){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(6.dp)){Text(g.getString("name"),fontSize=17.sp,fontWeight=FontWeight.Bold);Text((if(paused)"已暂停"else if(!g.optBoolean("enabled"))"已关闭"else g.optString("status"))+" · ${g.optInt("members")} 台设备",fontSize=13.sp,color=color.onSurface.copy(alpha=.65f));Text("最后同步  "+time(g.optLong("lastSync")),fontSize=12.sp,color=color.onSurface.copy(alpha=.55f))}}}
            Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){BasicComponent(title="管理同步组",onClick={tab=1})}
        }
        if(tab==1){Row(horizontalArrangement=Arrangement.spacedBy(12.dp)){Button(onClick={openGroup(1)},modifier=Modifier.weight(1f)){Text("创建同步组")};Button(onClick={openGroup(2)},modifier=Modifier.weight(1f)){Text("加入同步组")}};TextButton(text="扫描邀请二维码",onClick=::scan)
            for(g in groups){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(12.dp)){Row(verticalAlignment=Alignment.CenterVertically){Text(g.getString("name"),Modifier.weight(1f),fontSize=19.sp,fontWeight=FontWeight.Bold);if(!g.optBoolean("left"))Switch(g.optBoolean("enabled"),{toggleGroup(g,it)})};Text("${g.optInt("members")} 台设备 · "+(if(g.optString("transport")=="webdav")"WebDAV"else"专用中转")+(if(g.optBoolean("legacy"))" · 双设备组"else""),fontSize=13.sp,color=color.onSurface.copy(alpha=.6f));Text(g.optString("server"),fontSize=12.sp,color=color.onSurface.copy(alpha=.5f));Text("最后同步  "+time(g.optLong("lastSync")),fontSize=13.sp,color=color.onSurface.copy(alpha=.6f));if(g.optBoolean("legacy")){Text("此组保留早期配对。添加更多成员需重新建组并验证身份。",fontSize=12.sp,color=color.onSurface.copy(alpha=.55f));Row{TextButton(text="重新建组",onClick={openGroup(1,g)});TextButton(text="删除组",onClick={deleteLegacy(g)})}}else TextButton(text="管理成员 / 邀请设备",onClick={openGroup(0,g)})}}}
            if(groups.isEmpty())Text("尚无同步组。创建一个，或加入已有组。",fontSize=14.sp,color=color.onSurface.copy(alpha=.6f))
        }
            if(tab==2){Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Toggle("暂停全部同步",paused){value->runCatching{contentResolver.call(BridgeProvider.URI,"pause",value.toString(),null);refresh();startTransfers()}};Toggle("隐藏桌面图标",hidden,::hide);Toggle("显示附件接收常驻通知",statusNotificationsVisible){statusNotificationSettings()};Text("由系统管理。关闭“附件接收常驻提示”分类后，文件接收确认与图片预览仍会显示。",fontSize=12.sp,color=color.onSurface.copy(alpha=.6f),modifier=Modifier.padding(horizontal=20.dp).padding(bottom=14.dp));BasicComponent(title="开启附件接收通知",onClick={if(android.os.Build.VERSION.SDK_INT>=33)notificationPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)else startTransfers()});BasicComponent(title="清理未显示的同步组",onClick=::cleanHiddenGroups);BasicComponent(title="取消待发送附件",onClick={startForegroundService(Intent(this@MainActivity,TransferService::class.java).putExtra("action","cancel"))})};PollingSettings();Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Info("版本",BuildConfig.VERSION_NAME);Info("WebDAV","加密上传后通知，按设置的间隔检查");Info("文件目录","Download/ClipBridge");Info("图片相册","Pictures/ClipBridge")}}
        Spacer(Modifier.height(12.dp))
    }}}
    @Composable private fun PollingSettings(){
        var text by remember(textPollSeconds){mutableStateOf(textPollSeconds.toString())}
        var attachment by remember(attachmentPollSeconds){mutableStateOf(attachmentPollSeconds.toString())}
        val color=MiuixTheme.colorScheme
        Card(Modifier.fillMaxWidth(),cornerRadius=26.dp){Column(Modifier.padding(20.dp),verticalArrangement=Arrangement.spacedBy(16.dp)){
            Text("检查间隔",fontSize=18.sp,fontWeight=FontWeight.Bold)
            fun save(){val t=text.toIntOrNull();val a=attachment.toIntOrNull();if(t==null||a==null||t !in 1..300||a !in 1..300){toast("请输入 1～300 秒。");return};runCatching{contentResolver.call(BridgeProvider.URI,"poll-settings",null,Bundle().apply{putInt("textPollSeconds",t);putInt("attachmentPollSeconds",a)});refresh();toast("检查间隔已保存");startTransfers()}.onFailure{toast("检查间隔保存失败。")}}
            @Composable fun Field(title:String,value:String,change:(String)->Unit){Row(verticalAlignment=Alignment.CenterVertically){Text(title,Modifier.weight(1f),fontSize=15.sp);androidx.compose.foundation.text.BasicTextField(value=value,onValueChange={if(it.length<=3&&it.all(Char::isDigit))change(it)},singleLine=true,keyboardOptions=androidx.compose.foundation.text.KeyboardOptions(keyboardType=androidx.compose.ui.text.input.KeyboardType.Number),textStyle=androidx.compose.ui.text.TextStyle(fontSize=16.sp,color=color.onSurface),modifier=Modifier.width(74.dp).clip(RoundedCornerShape(10.dp)).background(color.onSurface.copy(alpha=.06f)).padding(12.dp));Text("秒",Modifier.padding(start=8.dp),fontSize=14.sp)}}
            Field("文本轮询间隔",text){text=it};Field("图片 / 文件检查间隔",attachment){attachment=it}
            Text("范围 1～300 秒。WebDAV 和同步组按此间隔检查文本；旧版专用中转即时接收。间隔越短请求越多。",fontSize=12.sp,color=color.onSurface.copy(alpha=.6f))
            TextButton(text="保存",onClick={save()},modifier=Modifier.align(Alignment.End))
        }}
    }
    @Composable private fun Toggle(title:String,checked:Boolean,change:(Boolean)->Unit){Row(Modifier.fillMaxWidth().padding(20.dp),verticalAlignment=Alignment.CenterVertically){Text(title,Modifier.weight(1f),fontSize=16.sp);Switch(checked,change)}}
    @Composable private fun Info(title:String,value:String){Row(Modifier.fillMaxWidth().padding(20.dp),horizontalArrangement=Arrangement.spacedBy(14.dp)){Text(title,fontSize=14.sp,modifier=Modifier.width(76.dp));Text(value,fontSize=14.sp,color=MiuixTheme.colorScheme.onSurface.copy(alpha=.6f),modifier=Modifier.weight(1f))}}
}
