CREATE TABLE books (
    id SERIAL PRIMARY KEY,
    title VARCHAR(150) NOT NULL,
    copies INT NOT NULL
);
CREATE TABLE loans (
    id SERIAL PRIMARY KEY,
    book_id INT REFERENCES books(id),
    reader VARCHAR(100) NOT NULL
);
INSERT INTO books (title, copies) VALUES
    ('books about cats', 3),
    ('book about nature', 2);