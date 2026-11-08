//! Recover stopped-run episode batches without starting a world or model.
//! No network send is performed; pending hashes remain unacknowledged.
use std::{env, io, path::PathBuf};
use bees_host::{durable_connection::DurableHiveMindConnection,
    notification_file::recover_command_outcomes};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = env::args_os().skip(1);
    let checkpoint = args.next().map(PathBuf::from).ok_or_else(||
        io::Error::other("usage: recover_outcomes checkpoint.json journal.jsonl [journal.jsonl ...]"))?;
    let journals: Vec<_> = args.map(PathBuf::from).collect();
    if journals.is_empty() { return Err(io::Error::other("at least one stopped journal is required").into()); }
    let mut connection = DurableHiveMindConnection::open_existing(checkpoint)
        .map_err(|error| io::Error::other(format!("checkpoint: {error:?}")))?;
    for path in journals {
        let recovery = recover_command_outcomes(&path, &mut connection)
            .map_err(|error| io::Error::other(format!("{}: {error:?}", path.display())))?;
        println!("{}", serde_json::json!({"journal": path, "recovery": recovery}));
    }
    println!("{}", serde_json::json!({"pending_store_hashes": connection.pending_store_hashes()
        .map_err(|error| io::Error::other(format!("connection: {error:?}")))?}));
    Ok(())
}
