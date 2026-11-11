fn main() {
    winresource::WindowsResource::new()
        .set_icon("../ClipBridge.Windows/App.ico")
        .set("ProductName", "ClipBridge")
        .set("FileDescription", "ClipBridge")
        .compile()
        .unwrap();
}
