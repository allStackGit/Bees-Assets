#[path = "../launch.rs"]
mod launch;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    launch::run()
}
