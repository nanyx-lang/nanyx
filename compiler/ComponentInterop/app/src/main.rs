//! Calls the Nanyx component's `acme:shapes/shapes`, which calls the Rust component's `acme:units/units` in turn, and
//! prints what it gives (../expected.txt).

wit_bindgen::generate!({ path: "wit", world: "app", generate_all });

use acme::shapes::shapes::{shift, summarize, Length, Point, Shape, Summary, Unit};

fn show(summary: Summary) {
    println!("{} {:.3} {:?}", summary.name, summary.area, summary.corners);
}

fn main() {
    show(summarize(&Shape::Circle(Length { value: 10.0, unit: Unit::Feet })));
    show(summarize(&Shape::Circle(Length { value: 2.0, unit: Unit::Metres })));
    show(summarize(&Shape::Rect((Point { x: 1.0, y: 2.0 }, Point { x: 4.0, y: 6.0 }))));
    show(summarize(&Shape::Polygon(vec![Point { x: 0.0, y: 0.0 }, Point { x: 1.0, y: 0.0 }, Point { x: 0.0, y: 1.0 }])));
    let shifted = shift(&[Point { x: 1.0, y: 1.0 }, Point { x: 2.0, y: 2.0 }], 0.5);
    println!("{:?}", shifted.iter().map(|p| (p.x, p.y)).collect::<Vec<_>>());
    println!("{:?}", shift(&[], 1.0).len());
}
