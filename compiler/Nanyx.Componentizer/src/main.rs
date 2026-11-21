//! Wraps a core module that follows wit-component's conventions as a component of the given world.
//!
//! Usage: `nanyx-componentizer <core.wasm> <wit-dir> <world> <component.wasm>`. It exits 0 having written the
//! component, or 1 having written why not to stderr.

use std::process::ExitCode;

use wit_component::{embed_component_metadata, ComponentEncoder, StringEncoding};
use wit_parser::Resolve;

fn componentize(core: &str, wit: &str, world: &str, out: &str) -> Result<(), String> {
    let mut module = std::fs::read(core).map_err(|e| format!("cannot read {core}: {e}"))?;
    let mut resolve = Resolve::default();
    let (package, _) = resolve.push_dir(wit).map_err(|e| format!("{e:#}"))?;
    let world = resolve.select_world(&[package], Some(world)).map_err(|e| format!("{e:#}"))?;
    embed_component_metadata(&mut module, &resolve, world, StringEncoding::UTF8, false).map_err(|e| format!("{e:#}"))?;
    let component = ComponentEncoder::default()
        .validate(true)
        .module(&module)
        .map_err(|e| format!("{e:#}"))?
        .encode()
        .map_err(|e| format!("{e:#}"))?;
    std::fs::write(out, component).map_err(|e| format!("cannot write {out}: {e}"))
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let [core, wit, world, out] = args.as_slice() else {
        eprintln!("usage: nanyx-componentizer <core.wasm> <wit-dir> <world> <component.wasm>");
        return ExitCode::from(2);
    };
    match componentize(core, wit, world, out) {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("{message}");
            ExitCode::FAILURE
        }
    }
}
